using System.Buffers.Binary;
using System.Net;
using System.Text;
using OpenGameAgent.Models;
using OpenGameAgent.Realtime;
using Xunit;

namespace OpenGameAgent.Providers.Local.Tests;

public sealed class LocalSpeechTests
{
    [Fact]
    public async Task RecognizerSendsBoundedWaveMultipartAndReturnsText()
    {
        var handler = new RecordingHandler(_ => Json("{\"text\":\"hello world\"}"));
        var recognizer = new LocalOpenAISpeechRecognizer(
            new LocalOpenAISpeechRecognizerOptions(
                new HttpClient(handler),
                new Uri("http://127.0.0.1:8000/v1"))
            {
                Model = "whisper-local",
            });

        var result = await recognizer.TranscribeAsync(
            new GameSpeechRecognitionRequest(
                "utterance-1",
                new byte[960],
                24_000,
                1,
                "en"),
            TestContext.Current.CancellationToken);

        Assert.Equal("hello world", result.Text);
        Assert.Equal("/v1/audio/transcriptions", Assert.Single(handler.Paths));
        var body = Assert.Single(handler.Bodies);
        var multipart = Encoding.Latin1.GetString(body);
        Assert.Contains("RIFF", multipart, StringComparison.Ordinal);
        Assert.Contains("whisper-local", multipart, StringComparison.Ordinal);
        Assert.Contains("speech.wav", multipart, StringComparison.Ordinal);
        Assert.Null(Assert.Single(handler.Authorizations));
    }

    [Fact]
    public async Task SynthesizerStreamsRawPcmWithPerRequestVoice()
    {
        var handler = new RecordingHandler(_ => Binary(new byte[1_920], "audio/pcm"));
        var synthesizer = new LocalOpenAISpeechSynthesizer(
            new LocalOpenAISpeechSynthesizerOptions(
                new HttpClient(handler),
                new Uri("http://127.0.0.1:8000/v1"))
            {
                Model = "kokoro",
                AudioFrameBytes = 480,
            });

        var frames = new List<RealtimeAudioFrame>();
        await foreach (var frame in synthesizer.SynthesizeAsync(
                           new GameSpeechSynthesisRequest("response", "item", "speak", "voice-a"),
                           TestContext.Current.CancellationToken))
        {
            frames.Add(frame);
        }

        Assert.Equal(4, frames.Count);
        Assert.All(frames, frame =>
        {
            Assert.Equal(24_000, frame.SampleRate);
            Assert.Equal("item", frame.ItemId);
        });
        Assert.Contains("\"voice\":\"voice-a\"", Encoding.UTF8.GetString(Assert.Single(handler.Bodies)), StringComparison.Ordinal);
        Assert.Contains("\"response_format\":\"pcm\"", Encoding.UTF8.GetString(handler.Bodies.Single()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SynthesizerValidatesWaveAndPreservesItsFormat()
    {
        var handler = new RecordingHandler(_ => Binary(Wave(new byte[640], 16_000, 2), "audio/wav"));
        var synthesizer = new LocalOpenAISpeechSynthesizer(
            new LocalOpenAISpeechSynthesizerOptions(
                new HttpClient(handler),
                new Uri("http://127.0.0.1:8000/v1"))
            {
                OutputFormat = LocalOpenAISpeechOutputFormat.Wave,
                AudioFrameBytes = 320,
            });

        var frames = new List<RealtimeAudioFrame>();
        await foreach (var frame in synthesizer.SynthesizeAsync(
                           new GameSpeechSynthesisRequest("response", "item", "speak", "voice"),
                           TestContext.Current.CancellationToken))
        {
            frames.Add(frame);
        }

        Assert.Equal(2, frames.Count);
        Assert.All(frames, frame =>
        {
            Assert.Equal(16_000, frame.SampleRate);
            Assert.Equal(2, frame.Channels);
        });
    }

    [Fact]
    public async Task ProviderErrorsDoNotEchoResponseBodyPromptOrCredential()
    {
        const string secret = "secret-local-key";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("secret-local-key private prompt", Encoding.UTF8, "text/plain"),
        });
        var options = new LocalOpenAISpeechSynthesizerOptions(
            new HttpClient(handler),
            new Uri("http://127.0.0.1:8000/v1"))
        {
            Authentication = new StaticGameProviderAuthentication(
                credential: new GameCredential(GameCredentialKind.ApiKey, secret)),
        };
        var synthesizer = new LocalOpenAISpeechSynthesizer(options);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in synthesizer.SynthesizeAsync(
                               new GameSpeechSynthesisRequest("response", "item", "private prompt", "voice"),
                               TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private prompt", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal("Bearer " + secret, Assert.Single(handler.Authorizations));
    }

    [Fact]
    public void RemotePlaintextRequiresBothExplicitOptIns()
    {
        Assert.Throws<ArgumentException>(() => new LocalOpenAISpeechRecognizer(
            new LocalOpenAISpeechRecognizerOptions(
                new HttpClient(),
                new Uri("http://example.com/v1"))));
        Assert.Throws<ArgumentException>(() => new LocalOpenAISpeechSynthesizer(
            new LocalOpenAISpeechSynthesizerOptions(
                new HttpClient(),
                new Uri("http://example.com/v1"))
            {
                AllowRemoteEndpoint = true,
            }));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Binary(byte[] body, string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body)
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) },
        },
    };

    private static byte[] Wave(byte[] pcm16, int sampleRate, int channels)
    {
        var result = new byte[44 + pcm16.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(result, 0);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), result.Length - 8);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(result, 8);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(28), sampleRate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(32), (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(result, 36);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(40), pcm16.Length);
        pcm16.CopyTo(result, 44);
        return result;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public List<string> Paths { get; } = new();
        public List<byte[]> Bodies { get; } = new();
        public List<string?> Authorizations { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Authorizations.Add(request.Headers.Authorization?.ToString());
            Bodies.Add(request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken));
            var response = _respond(request);
            response.RequestMessage = request;
            return response;
        }
    }
}
