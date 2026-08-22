using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Models;
using OpenGameAgent.Realtime;

namespace OpenGameAgent.Providers.Local;

public sealed class LocalOpenAISpeechRecognizerOptions
{
    public LocalOpenAISpeechRecognizerOptions(HttpClient httpClient, Uri endpoint)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public HttpClient HttpClient { get; }

    public Uri Endpoint { get; set; }

    public string Model { get; set; } = "whisper-1";

    public IGameProviderAuthentication Authentication { get; set; } =
        new StaticGameProviderAuthentication();

    public bool AllowRemoteEndpoint { get; set; }

    public bool AllowInsecureRemoteHttp { get; set; }

    public int MaximumRequestBytes { get; set; } = 32_000_000;

    public int MaximumResponseBytes { get; set; } = 4_000_000;

    public int TimeoutMilliseconds { get; set; } = 120_000;
}

/// <summary>
/// Transcribes PCM16 through a bounded OpenAI-compatible audio/transcriptions endpoint.
/// It is suitable for explicit loopback services such as LocalAI, Speaches, or a host adapter.
/// </summary>
public sealed class LocalOpenAISpeechRecognizer : IGameSpeechRecognizer
{
    private readonly RecognizerSettings _settings;

    public LocalOpenAISpeechRecognizer(LocalOpenAISpeechRecognizerOptions options)
    {
        _settings = RecognizerSettings.Create(options);
    }

    public async ValueTask<GameSpeechRecognitionResult> TranscribeAsync(
        GameSpeechRecognitionRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var wave = LocalSpeechWave.Create(request.Pcm16.Span, request.SampleRate, request.Channels);
        if (wave.Length > _settings.MaximumRequestBytes)
        {
            throw new InvalidDataException("The local speech recognition request exceeded its configured limit.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_settings.TimeoutMilliseconds);
        var authentication = await _settings.Authentication.ResolveAsync(timeout.Token).ConfigureAwait(false);
        var endpoint = LocalSpeechHttp.Endpoint(
            authentication?.BaseUrl ?? _settings.Endpoint,
            "audio/transcriptions",
            _settings.AllowRemoteEndpoint,
            _settings.AllowInsecureRemoteHttp);
        using var content = new MultipartFormDataContent("oga-speech-" + Guid.NewGuid().ToString("N"));
        using var audio = new ByteArrayContent(wave);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audio, "file", "speech.wav");
        content.Add(new StringContent(_settings.Model, Encoding.UTF8), "model");
        content.Add(new StringContent("json", Encoding.UTF8), "response_format");
        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            content.Add(new StringContent(request.Language!, Encoding.UTF8), "language");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        LocalSpeechHttp.ApplyAuthentication(message, authentication);
        using var response = await _settings.HttpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        LocalSpeechHttp.ValidateResponseOrigin(endpoint, response);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                "The local speech recognition service returned HTTP "
                + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ".");
        }

        var bytes = await LocalSpeechHttp.ReadBoundedAsync(
            response.Content,
            _settings.MaximumResponseBytes,
            timeout.Token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("text", out var text)
            || text.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(text.GetString()))
        {
            throw new InvalidDataException("The local speech recognition response was invalid.");
        }

        return new GameSpeechRecognitionResult(text.GetString()!);
    }

    private sealed class RecognizerSettings
    {
        private RecognizerSettings(LocalOpenAISpeechRecognizerOptions options)
        {
            HttpClient = options.HttpClient;
            Endpoint = options.Endpoint;
            Model = options.Model;
            Authentication = options.Authentication;
            AllowRemoteEndpoint = options.AllowRemoteEndpoint;
            AllowInsecureRemoteHttp = options.AllowInsecureRemoteHttp;
            MaximumRequestBytes = options.MaximumRequestBytes;
            MaximumResponseBytes = options.MaximumResponseBytes;
            TimeoutMilliseconds = options.TimeoutMilliseconds;
        }

        public HttpClient HttpClient { get; }
        public Uri Endpoint { get; }
        public string Model { get; }
        public IGameProviderAuthentication Authentication { get; }
        public bool AllowRemoteEndpoint { get; }
        public bool AllowInsecureRemoteHttp { get; }
        public int MaximumRequestBytes { get; }
        public int MaximumResponseBytes { get; }
        public int TimeoutMilliseconds { get; }

        public static RecognizerSettings Create(LocalOpenAISpeechRecognizerOptions options)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            LocalSpeechHttp.ValidateOptions(
                options.Endpoint,
                options.Model,
                options.Authentication,
                options.AllowRemoteEndpoint,
                options.AllowInsecureRemoteHttp,
                options.MaximumRequestBytes,
                options.MaximumResponseBytes,
                options.TimeoutMilliseconds);
            return new RecognizerSettings(options);
        }
    }
}

public enum LocalOpenAISpeechOutputFormat
{
    Pcm16,
    Wave,
}

public sealed class LocalOpenAISpeechSynthesizerOptions
{
    public LocalOpenAISpeechSynthesizerOptions(HttpClient httpClient, Uri endpoint)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public HttpClient HttpClient { get; }

    public Uri Endpoint { get; set; }

    public string Model { get; set; } = "tts-1";

    public IGameProviderAuthentication Authentication { get; set; } =
        new StaticGameProviderAuthentication();

    public LocalOpenAISpeechOutputFormat OutputFormat { get; set; } = LocalOpenAISpeechOutputFormat.Pcm16;

    public int RawPcmSampleRate { get; set; } = 24_000;

    public int RawPcmChannels { get; set; } = 1;

    public int AudioFrameBytes { get; set; } = 24_000;

    public bool AllowRemoteEndpoint { get; set; }

    public bool AllowInsecureRemoteHttp { get; set; }

    public int MaximumRequestBytes { get; set; } = 1_000_000;

    public int MaximumResponseBytes { get; set; } = 64_000_000;

    public int TimeoutMilliseconds { get; set; } = 120_000;
}

/// <summary>Streams raw PCM16 or validates bounded PCM16 WAV output from an OpenAI-compatible speech endpoint.</summary>
public sealed class LocalOpenAISpeechSynthesizer : IGameSpeechSynthesizer
{
    private readonly SynthesizerSettings _settings;

    public LocalOpenAISpeechSynthesizer(LocalOpenAISpeechSynthesizerOptions options)
    {
        _settings = SynthesizerSettings.Create(options);
    }

    public async IAsyncEnumerable<RealtimeAudioFrame> SynthesizeAsync(
        GameSpeechSynthesisRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = _settings.Model,
            input = request.Text,
            voice = request.Voice,
            response_format = _settings.OutputFormat == LocalOpenAISpeechOutputFormat.Pcm16 ? "pcm" : "wav",
        });
        if (payload.Length > _settings.MaximumRequestBytes)
        {
            throw new InvalidDataException("The local speech synthesis request exceeded its configured limit.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_settings.TimeoutMilliseconds);
        var authentication = await _settings.Authentication.ResolveAsync(timeout.Token).ConfigureAwait(false);
        var endpoint = LocalSpeechHttp.Endpoint(
            authentication?.BaseUrl ?? _settings.Endpoint,
            "audio/speech",
            _settings.AllowRemoteEndpoint,
            _settings.AllowInsecureRemoteHttp);
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(payload),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        LocalSpeechHttp.ApplyAuthentication(message, authentication);
        using var response = await _settings.HttpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        LocalSpeechHttp.ValidateResponseOrigin(endpoint, response);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                "The local speech synthesis service returned HTTP "
                + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ".");
        }

        if (response.Content.Headers.ContentType?.MediaType is { } mediaType
            && !mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The local speech synthesis service returned a non-audio response.");
        }

        if (_settings.OutputFormat == LocalOpenAISpeechOutputFormat.Wave)
        {
            var bytes = await LocalSpeechHttp.ReadBoundedAsync(
                response.Content,
                _settings.MaximumResponseBytes,
                timeout.Token).ConfigureAwait(false);
            var wave = LocalSpeechWave.Parse(bytes);
            foreach (var frame in Frames(wave.Pcm16, wave.SampleRate, wave.Channels, request.ItemId))
            {
                yield return frame;
            }

            yield break;
        }

        await foreach (var frame in ReadRawPcmAsync(response.Content, request.ItemId, timeout.Token)
                           .ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    private async IAsyncEnumerable<RealtimeAudioFrame> ReadRawPcmAsync(
        HttpContent content,
        string itemId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 and var length && length > _settings.MaximumResponseBytes)
        {
            throw new InvalidDataException("The local speech synthesis response exceeded its configured limit.");
        }

        using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
        var buffer = new byte[_settings.AudioFrameBytes + 1];
        var pending = 0;
        var total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(
                buffer,
                pending,
                _settings.AudioFrameBytes - pending,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var count = pending + read;
            total = checked(total + read);
            if (total > _settings.MaximumResponseBytes)
            {
                throw new InvalidDataException("The local speech synthesis response exceeded its configured limit.");
            }

            var even = count - count % 2;
            if (even > 0)
            {
                var bytes = new byte[even];
                Buffer.BlockCopy(buffer, 0, bytes, 0, even);
                yield return new RealtimeAudioFrame(
                    bytes,
                    _settings.RawPcmSampleRate,
                    _settings.RawPcmChannels,
                    itemId);
            }

            pending = count - even;
            if (pending > 0)
            {
                buffer[0] = buffer[count - 1];
            }
        }

        if (pending != 0)
        {
            throw new InvalidDataException("The local speech synthesis response ended with an incomplete PCM16 sample.");
        }
    }

    private IEnumerable<RealtimeAudioFrame> Frames(
        ReadOnlyMemory<byte> bytes,
        int sampleRate,
        int channels,
        string itemId)
    {
        var alignment = checked(2 * channels);
        var maximum = _settings.AudioFrameBytes - _settings.AudioFrameBytes % alignment;
        for (var offset = 0; offset < bytes.Length; offset += maximum)
        {
            var count = Math.Min(maximum, bytes.Length - offset);
            yield return new RealtimeAudioFrame(
                bytes.Slice(offset, count).ToArray(),
                sampleRate,
                channels,
                itemId);
        }
    }

    private sealed class SynthesizerSettings
    {
        private SynthesizerSettings(LocalOpenAISpeechSynthesizerOptions options)
        {
            HttpClient = options.HttpClient;
            Endpoint = options.Endpoint;
            Model = options.Model;
            Authentication = options.Authentication;
            OutputFormat = options.OutputFormat;
            RawPcmSampleRate = options.RawPcmSampleRate;
            RawPcmChannels = options.RawPcmChannels;
            AudioFrameBytes = options.AudioFrameBytes;
            AllowRemoteEndpoint = options.AllowRemoteEndpoint;
            AllowInsecureRemoteHttp = options.AllowInsecureRemoteHttp;
            MaximumRequestBytes = options.MaximumRequestBytes;
            MaximumResponseBytes = options.MaximumResponseBytes;
            TimeoutMilliseconds = options.TimeoutMilliseconds;
        }

        public HttpClient HttpClient { get; }
        public Uri Endpoint { get; }
        public string Model { get; }
        public IGameProviderAuthentication Authentication { get; }
        public LocalOpenAISpeechOutputFormat OutputFormat { get; }
        public int RawPcmSampleRate { get; }
        public int RawPcmChannels { get; }
        public int AudioFrameBytes { get; }
        public bool AllowRemoteEndpoint { get; }
        public bool AllowInsecureRemoteHttp { get; }
        public int MaximumRequestBytes { get; }
        public int MaximumResponseBytes { get; }
        public int TimeoutMilliseconds { get; }

        public static SynthesizerSettings Create(LocalOpenAISpeechSynthesizerOptions options)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            LocalSpeechHttp.ValidateOptions(
                options.Endpoint,
                options.Model,
                options.Authentication,
                options.AllowRemoteEndpoint,
                options.AllowInsecureRemoteHttp,
                options.MaximumRequestBytes,
                options.MaximumResponseBytes,
                options.TimeoutMilliseconds);
            if (!Enum.IsDefined(typeof(LocalOpenAISpeechOutputFormat), options.OutputFormat)
                || options.RawPcmSampleRate is < 8_000 or > 192_000
                || options.RawPcmChannels is < 1 or > 8
                || options.AudioFrameBytes is < 2 or > 4_194_304
                || options.AudioFrameBytes % 2 != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options));
            }

            return new SynthesizerSettings(options);
        }
    }
}

internal static class LocalSpeechHttp
{
    public static void ValidateOptions(
        Uri endpoint,
        string model,
        IGameProviderAuthentication authentication,
        bool allowRemote,
        bool allowInsecureRemoteHttp,
        int maximumRequestBytes,
        int maximumResponseBytes,
        int timeoutMilliseconds)
    {
        ValidateEndpoint(endpoint, allowRemote, allowInsecureRemoteHttp);
        if (string.IsNullOrWhiteSpace(model)
            || model.Length > 256
            || model.Any(char.IsControl)
            || authentication is null
            || maximumRequestBytes is < 2 or > 256_000_000
            || maximumResponseBytes is < 2 or > 512_000_000
            || timeoutMilliseconds is < 100 or > 600_000)
        {
            throw new ArgumentOutOfRangeException(nameof(model));
        }
    }

    public static Uri Endpoint(
        Uri baseUrl,
        string suffix,
        bool allowRemote,
        bool allowInsecureRemoteHttp)
    {
        ValidateEndpoint(baseUrl, allowRemote, allowInsecureRemoteHttp);
        var path = baseUrl.AbsolutePath.TrimEnd('/');
        var expected = "/" + suffix;
        if (!path.EndsWith(expected, StringComparison.OrdinalIgnoreCase))
        {
            path = path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? path + expected
                : path + "/v1" + expected;
        }

        return new UriBuilder(baseUrl) { Path = path }.Uri;
    }

    public static void ApplyAuthentication(
        HttpRequestMessage request,
        GameProviderAuthResolution? authentication)
    {
        if (authentication?.Credential is { } credential)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Secret);
        }

        foreach (var pair in authentication?.Headers ?? new Dictionary<string, string?>())
        {
            if (pair.Value is not null && !request.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
            {
                throw new InvalidOperationException("A local speech authentication header was invalid.");
            }
        }
    }

    public static void ValidateResponseOrigin(Uri endpoint, HttpResponseMessage response)
    {
        if ((int)response.StatusCode is >= 300 and <= 399)
        {
            throw new InvalidDataException("The local speech service refused a redirect response.");
        }

        if (response.RequestMessage?.RequestUri is { } final
            && (!string.Equals(endpoint.Scheme, final.Scheme, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(endpoint.Host, final.Host, StringComparison.OrdinalIgnoreCase)
                || endpoint.Port != final.Port))
        {
            throw new InvalidDataException("The local speech service redirected across origins.");
        }
    }

    public static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 and var length && length > maximumBytes)
        {
            throw new InvalidDataException("The local speech response exceeded its configured limit.");
        }

        using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new InvalidDataException("The local speech response exceeded its configured limit.");
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static void ValidateEndpoint(Uri endpoint, bool allowRemote, bool allowInsecureRemoteHttp)
    {
        if (!endpoint.IsAbsoluteUri
            || endpoint.UserInfo.Length > 0
            || endpoint.Fragment.Length > 0
            || endpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("A valid local speech HTTP endpoint is required.", nameof(endpoint));
        }

        if (!endpoint.IsLoopback && !allowRemote)
        {
            throw new ArgumentException("Local speech endpoints are loopback-only unless remote access is enabled.", nameof(endpoint));
        }

        if (!endpoint.IsLoopback && endpoint.Scheme == Uri.UriSchemeHttp && !allowInsecureRemoteHttp)
        {
            throw new ArgumentException("Remote speech endpoints must use HTTPS.", nameof(endpoint));
        }
    }
}

internal static class LocalSpeechWave
{
    public static byte[] Create(ReadOnlySpan<byte> pcm16, int sampleRate, int channels)
    {
        var result = new byte[checked(44 + pcm16.Length)];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(result, 0);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), result.Length - 8);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(result, 8);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(22), checked((short)channels));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(28), checked(sampleRate * channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(32), checked((short)(channels * 2)));
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(result, 36);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(40), pcm16.Length);
        pcm16.CopyTo(result.AsSpan(44));
        return result;
    }

    public static ParsedWave Parse(ReadOnlyMemory<byte> bytes)
    {
        var span = bytes.Span;
        if (span.Length < 44
            || !span.Slice(0, 4).SequenceEqual(Encoding.ASCII.GetBytes("RIFF"))
            || !span.Slice(8, 4).SequenceEqual(Encoding.ASCII.GetBytes("WAVE")))
        {
            throw new InvalidDataException("The local speech WAV response was invalid.");
        }

        int? sampleRate = null;
        int? channels = null;
        ReadOnlyMemory<byte>? data = null;
        var offset = 12;
        while (offset + 8 <= span.Length)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset + 4, 4));
            if (size < 0 || offset + 8L + size > span.Length)
            {
                throw new InvalidDataException("The local speech WAV response contained an invalid chunk.");
            }

            var id = Encoding.ASCII.GetString(span.Slice(offset, 4).ToArray());
            var payload = offset + 8;
            if (id == "fmt ")
            {
                if (size < 16
                    || BinaryPrimitives.ReadInt16LittleEndian(span.Slice(payload, 2)) != 1
                    || BinaryPrimitives.ReadInt16LittleEndian(span.Slice(payload + 14, 2)) != 16)
                {
                    throw new InvalidDataException("The local speech WAV response was not PCM16.");
                }

                channels = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(payload + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(payload + 4, 4));
            }
            else if (id == "data")
            {
                data = bytes.Slice(payload, size);
            }

            offset = checked(payload + size + size % 2);
        }

        if (sampleRate is not (>= 8_000 and <= 192_000)
            || channels is not (>= 1 and <= 8)
            || data is not { } pcm
            || pcm.IsEmpty
            || pcm.Length % (2 * channels.Value) != 0)
        {
            throw new InvalidDataException("The local speech WAV response was incomplete or unsupported.");
        }

        return new ParsedWave(pcm, sampleRate.Value, channels.Value);
    }

    internal readonly struct ParsedWave
    {
        public ParsedWave(ReadOnlyMemory<byte> pcm16, int sampleRate, int channels)
        {
            Pcm16 = pcm16;
            SampleRate = sampleRate;
            Channels = channels;
        }

        public ReadOnlyMemory<byte> Pcm16 { get; }
        public int SampleRate { get; }
        public int Channels { get; }
    }
}
