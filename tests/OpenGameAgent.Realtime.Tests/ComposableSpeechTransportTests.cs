using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Realtime.Tests;

public sealed class ComposableSpeechTransportTests
{
    [Fact]
    public async Task EnergyDetectorProducesStableSpeechBoundaries()
    {
        await using var detector = new EnergyGameVoiceActivityDetector(
            new EnergyGameVoiceActivityDetectorOptions
            {
                RootMeanSquareThreshold = 0.01,
                MinimumSpeechMilliseconds = 20,
                StopAfterSilenceMilliseconds = 20,
            });
        var speech = Frame(1_000, sample: 10_000);
        var silence = Frame(1_000, sample: 0);

        Assert.True((await detector.AnalyzeAsync(speech, TestContext.Current.CancellationToken)).SpeechStarted);
        Assert.True((await detector.AnalyzeAsync(silence, TestContext.Current.CancellationToken)).SpeechStopped);
        detector.Reset();
        Assert.True((await detector.AnalyzeAsync(speech, TestContext.Current.CancellationToken)).SpeechStarted);
    }

    [Fact]
    public async Task SpeechBecomesTranscriptAndHandoffWithoutASecondAgentLoop()
    {
        var recognizer = new RecordingRecognizer("inspect the valley");
        var transport = Transport(recognizer, new EchoSynthesizer());
        await using var session = await transport.ConnectAsync(
            new RealtimeConversationOptions(),
            TestContext.Current.CancellationToken);

        await session.SendAudioAsync(Frame(480), TestContext.Current.CancellationToken);
        await session.SendAudioAsync(Frame(480), TestContext.Current.CancellationToken);

        var events = await ReadUntilAsync(
            session,
            value => value.Kind == RealtimeConversationEventKind.HandoffRequested,
            TestContext.Current.CancellationToken);

        Assert.Contains(events, value => value.Kind == RealtimeConversationEventKind.InputSpeechStarted);
        Assert.Contains(events, value => value.Kind == RealtimeConversationEventKind.InputSpeechStopped);
        Assert.Contains(events, value => value.Kind == RealtimeConversationEventKind.InputTranscriptDone);
        var handoff = Assert.Single(events, value => value.Handoff is not null).Handoff!;
        Assert.Equal("inspect the valley", handoff.Transcript);
        Assert.Single(recognizer.Requests);
        Assert.Equal(960, recognizer.Requests.Single().Pcm16.Length);
    }

    [Fact]
    public async Task HandoffTextStreamsTranscriptAudioAndOneTerminalResponse()
    {
        var synth = new EchoSynthesizer();
        var transport = Transport(new RecordingRecognizer("unused"), synth);
        await using var session = await transport.ConnectAsync(
            new RealtimeConversationOptions { Voice = "npc-voice" },
            TestContext.Current.CancellationToken);

        await session.SendHandoffAsync(
            "response-1",
            "hello ",
            RealtimeHandoffPhase.Commentary,
            completed: false,
            TestContext.Current.CancellationToken);
        await session.SendHandoffAsync(
            "response-1",
            "world",
            RealtimeHandoffPhase.Final,
            completed: true,
            TestContext.Current.CancellationToken);

        var events = await ReadUntilAsync(
            session,
            value => value.Kind == RealtimeConversationEventKind.ResponseDone,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, events.Count(value => value.Kind == RealtimeConversationEventKind.ResponseStarted));
        Assert.Equal(1, events.Count(value => value.Kind == RealtimeConversationEventKind.ResponseDone));
        Assert.Equal(2, events.Count(value => value.Kind == RealtimeConversationEventKind.AudioOutput));
        Assert.Equal(
            "hello world",
            events.Single(value => value.Kind == RealtimeConversationEventKind.OutputTranscriptDone).Text);
        Assert.All(synth.Requests, request => Assert.Equal("npc-voice", request.Voice));
    }

    [Fact]
    public async Task BargeInCancelsStreamingSpeechAndDropsQueuedSegments()
    {
        var synth = new BlockingSynthesizer();
        var transport = Transport(new RecordingRecognizer("unused"), synth);
        await using var session = await transport.ConnectAsync(
            new RealtimeConversationOptions(),
            TestContext.Current.CancellationToken);
        await session.SendHandoffAsync(
            "response-1",
            "first",
            RealtimeHandoffPhase.Commentary,
            completed: false,
            TestContext.Current.CancellationToken);
        await session.SendHandoffAsync(
            "response-1",
            "queued",
            RealtimeHandoffPhase.Final,
            completed: true,
            TestContext.Current.CancellationToken);

        await synth.FirstFrameSent.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await session.CancelResponseAsync(TestContext.Current.CancellationToken);
        await synth.Cancelled.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var events = await ReadUntilAsync(
            session,
            value => value.Kind == RealtimeConversationEventKind.ResponseCancelled,
            TestContext.Current.CancellationToken);
        Assert.Contains(events, value => value.Kind == RealtimeConversationEventKind.AudioOutput);
        Assert.DoesNotContain(events, value => value.Kind == RealtimeConversationEventKind.ResponseDone);
        Assert.DoesNotContain(synth.Requests, request => request.Text == "queued");
    }

    [Fact]
    public async Task OversizedUtteranceFailsClosedBeforeRecognition()
    {
        var recognizer = new RecordingRecognizer("must not run");
        var transport = Transport(
            recognizer,
            new EchoSynthesizer(),
            new ComposableRealtimeTransportOptions
            {
                VoiceActivityDetectorFactory = static () => new StartStopDetector(),
                MaximumUtteranceBytes = 500,
            });
        await using var session = await transport.ConnectAsync(
            new RealtimeConversationOptions(),
            TestContext.Current.CancellationToken);

        await session.SendAudioAsync(Frame(480), TestContext.Current.CancellationToken);
        await session.SendAudioAsync(Frame(480), TestContext.Current.CancellationToken);
        var events = await ReadUntilAsync(
            session,
            value => value.Kind == RealtimeConversationEventKind.InputSpeechStopped,
            TestContext.Current.CancellationToken);

        Assert.Contains(events, value => value.Error == "speech-utterance-limit");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(recognizer.Requests);
    }

    [Fact]
    public async Task ConversationBridgeRunsTheExistingRuntimeAndReturnsLocalSpeech()
    {
        var provider = new CapturingProvider();
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "agent-model")
        {
            RoutePolicy = new AutomaticGameRoutePolicy(new Dictionary<string, GameRouteDecision>
            {
                ["voice"] = GameRouteDecision.Agent("voice"),
            }),
        });
        var transport = Transport(new RecordingRecognizer("look around"), new EchoSynthesizer());
        await using var manager = new RealtimeConversationManager(transport);
        await using var bridge = new GameRealtimeAgentBridge(
            runtime,
            manager,
            new GameSessionKey("save", "npc"),
            (handoff, _) => new ValueTask<GameInput>(new GameInput(
                "save",
                "npc",
                "voice",
                "{\"text\":\"" + handoff.Transcript + "\"}",
                new GameMoment("timeline", 10),
                handoff.HandoffId)));
        var audio = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = manager.RegisterHandler((value, _) =>
        {
            if (value.Kind == RealtimeConversationEventKind.AudioOutput)
            {
                audio.TrySetResult(true);
            }

            return default;
        });
        await manager.StartAsync(new RealtimeConversationOptions(), TestContext.Current.CancellationToken);

        Assert.True(manager.TrySendAudio(Frame(480)));
        Assert.True(manager.TrySendAudio(Frame(480)));
        await audio.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, provider.RequestCount);
        Assert.Contains(provider.LastRequest!.Messages, message =>
            message.Content.OfType<JsonContent>().Any(json => json.Json.Contains("look around", StringComparison.Ordinal)));
    }

    private static ComposableRealtimeTransport Transport(
        IGameSpeechRecognizer recognizer,
        IGameSpeechSynthesizer synthesizer,
        ComposableRealtimeTransportOptions? options = null) => new(
        recognizer,
        synthesizer,
        options ?? new ComposableRealtimeTransportOptions
        {
            VoiceActivityDetectorFactory = static () => new StartStopDetector(),
        });

    private static RealtimeAudioFrame Frame(int bytes, short sample = 1_000)
    {
        var pcm = new byte[bytes];
        for (var offset = 0; offset < pcm.Length; offset += 2)
        {
            pcm[offset] = (byte)(sample & 0xff);
            pcm[offset + 1] = (byte)(sample >> 8);
        }

        return new RealtimeAudioFrame(pcm);
    }

    private static async Task<IReadOnlyList<RealtimeConversationEvent>> ReadUntilAsync(
        IRealtimeTransportSession session,
        Func<RealtimeConversationEvent, bool> stop,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var result = new List<RealtimeConversationEvent>();
        await foreach (var value in session.ReadEventsAsync(timeout.Token))
        {
            result.Add(value);
            if (stop(value))
            {
                return result;
            }
        }

        throw new InvalidOperationException("The expected realtime event was not observed.");
    }

    private sealed class StartStopDetector : IGameVoiceActivityDetector
    {
        private int _calls;

        public ValueTask<GameVoiceActivityDecision> AnalyzeAsync(
            RealtimeAudioFrame frame,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _calls);
            return new ValueTask<GameVoiceActivityDecision>(new GameVoiceActivityDecision(
                containsSpeech: call == 1,
                speechStarted: call == 1,
                speechStopped: call == 2));
        }

        public void Reset() => _calls = 0;

        public ValueTask DisposeAsync() => default;
    }

    private sealed class RecordingRecognizer : IGameSpeechRecognizer
    {
        private readonly string _text;

        public RecordingRecognizer(string text)
        {
            _text = text;
        }

        public ConcurrentBag<GameSpeechRecognitionRequest> Requests { get; } = new();

        public ValueTask<GameSpeechRecognitionResult> TranscribeAsync(
            GameSpeechRecognitionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return new ValueTask<GameSpeechRecognitionResult>(new GameSpeechRecognitionResult(_text, 0.9));
        }
    }

    private sealed class EchoSynthesizer : IGameSpeechSynthesizer
    {
        public ConcurrentBag<GameSpeechSynthesisRequest> Requests { get; } = new();

        public async IAsyncEnumerable<RealtimeAudioFrame> SynthesizeAsync(
            GameSpeechSynthesisRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return Frame(480);
        }
    }

    private sealed class BlockingSynthesizer : IGameSpeechSynthesizer
    {
        public ConcurrentBag<GameSpeechSynthesisRequest> Requests { get; } = new();
        public TaskCompletionSource<bool> FirstFrameSent { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<RealtimeAudioFrame> SynthesizeAsync(
            GameSpeechSynthesisRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            yield return Frame(480);
            FirstFrameSent.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Cancelled.TrySetResult(true);
                }
            }
        }
    }

    private sealed class CapturingProvider : IModelProvider
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);
        public ModelRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastRequest = request;
            Interlocked.Increment(ref _requestCount);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("The valley is clear.") },
                ModelStopReason.Stop));
        }
    }
}
