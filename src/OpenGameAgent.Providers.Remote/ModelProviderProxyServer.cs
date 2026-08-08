using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Providers.Remote;

public sealed class ModelProviderProxyServer
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly IModelProvider _provider;
    private readonly ServerSettings _settings;

    public ModelProviderProxyServer(
        IModelProvider provider,
        ModelProviderProxyServerOptions? options = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _settings = ServerSettings.Validate(options ?? new ModelProviderProxyServerOptions());
    }

    public async Task<HttpResponseMessage> HandleAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ModelRequest? modelRequest = null;
        string? preflightError = null;
        if (request.Method != HttpMethod.Post)
        {
            preflightError = "The remote provider proxy only accepts POST requests.";
        }
        else if (!Authenticate(request))
        {
            preflightError = "Unauthorized remote provider request.";
        }
        else if (request.Content is null
                 || !string.Equals(
                     request.Content.Headers.ContentType?.MediaType,
                     "application/json",
                     StringComparison.OrdinalIgnoreCase))
        {
            preflightError = "The remote provider request must use application/json.";
        }
        else
        {
            try
            {
                var requestBody = await ReadRequestBodyAsync(request.Content, cancellationToken).ConfigureAwait(false);
                modelRequest = ProxyWire.ParseRequest(requestBody, _settings.MaximumJsonDepth);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or DecoderFallbackException)
            {
                preflightError = exception.Message;
            }
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ProxySseContent(
                (stream, token) => WriteResponseAsync(stream, modelRequest, preflightError, token),
                cancellationToken),
        };
        response.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        return response;
    }

    public Task WriteAsync(
        Stream output,
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        if (output is null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return WriteResponseAsync(output, request, null, cancellationToken);
    }

    private async Task WriteResponseAsync(
        Stream output,
        ModelRequest? request,
        string? preflightError,
        CancellationToken cancellationToken)
    {
        var writer = new ProxySseWriter(
            output,
            _settings.MaximumEventBytes,
            _settings.MaximumResponseBytes);
        var setupWritten = false;
        ModelResponse? lastPartial = null;
        try
        {
            if (preflightError is not null || request is null)
            {
                await writer.WriteAsync(Setup(DefaultPartial(request)), cancellationToken).ConfigureAwait(false);
                setupWritten = true;
                await writer.WriteAsync(
                    Terminal(ErrorResponse(null, preflightError ?? "The remote provider request is invalid.", request)),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var decoder = new RemoteStreamDecoder();
            WireFrame? pendingTerminal = null;
            var eventCount = 0;
            await using var enumerator = _provider
                .StreamAsync(request, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                eventCount++;
                if (eventCount > _settings.MaximumEvents)
                {
                    throw new InvalidDataException("The upstream model provider exceeded the configured event limit.");
                }

                var modelEvent = enumerator.Current
                                 ?? throw new InvalidDataException("The upstream model provider emitted a null event.");
                if (!setupWritten)
                {
                    if (modelEvent.Kind != ModelStreamEventKind.Started || modelEvent.Partial is null)
                    {
                        throw new InvalidDataException("The upstream model provider must begin with a start event.");
                    }

                    var setup = Setup(modelEvent.Partial);
                    decoder.Decode(setup);
                    await writer.WriteAsync(setup, cancellationToken).ConfigureAwait(false);
                    setupWritten = true;
                }

                var frame = Frame(modelEvent);
                var decoded = decoder.Decode(frame);
                ValidateRoundTrip(modelEvent, decoded);
                if (modelEvent.IsTerminal)
                {
                    if (pendingTerminal is not null)
                    {
                        throw new InvalidDataException("The upstream model provider emitted more than one terminal event.");
                    }

                    pendingTerminal = frame;
                    continue;
                }

                if (pendingTerminal is not null)
                {
                    throw new InvalidDataException("The upstream model provider emitted an event after its terminal event.");
                }

                lastPartial = modelEvent.Partial;
                await writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            }

            if (!setupWritten)
            {
                throw new InvalidDataException("The upstream model provider ended before its start event.");
            }

            if (pendingTerminal is null)
            {
                throw new InvalidDataException("The upstream model provider ended without a terminal event.");
            }

            await writer.WriteAsync(pendingTerminal, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (!setupWritten)
            {
                await writer.WriteAsync(Setup(DefaultPartial(request)), cancellationToken).ConfigureAwait(false);
            }

            var error = ErrorResponse(lastPartial, exception.Message, request);
            await writer.WriteAsync(Terminal(error), cancellationToken).ConfigureAwait(false);
        }
    }

    private static WireFrame Setup(ModelResponse partial) => new()
    {
        Type = ProxyWire.SetupFrame,
        Version = ProxyWire.Version,
        Response = ProxyWire.Response(partial),
    };

    private static WireFrame Terminal(ModelResponse response) => new()
    {
        Type = ProxyWire.TerminalFrame,
        Response = ProxyWire.Response(response),
    };

    private static WireFrame Frame(ModelStreamEvent modelEvent)
    {
        if (modelEvent.IsTerminal)
        {
            return Terminal(modelEvent.Response
                            ?? throw new InvalidDataException("An upstream terminal event requires a response."));
        }

        var partial = modelEvent.Partial
                      ?? throw new InvalidDataException("An upstream update event requires a partial response.");
        if (partial.StopReason != ModelStopReason.Pending)
        {
            throw new InvalidDataException("An upstream update event must contain a pending partial response.");
        }

        AgentContent? content = null;
        if (modelEvent.Kind != ModelStreamEventKind.Started)
        {
            if (modelEvent.ContentIndex < 0 || modelEvent.ContentIndex >= partial.Content.Count)
            {
                throw new InvalidDataException("An upstream content event contains an invalid content index.");
            }

            var partialContent = partial.Content[modelEvent.ContentIndex];
            if (modelEvent.Kind is ModelStreamEventKind.TextStarted
                or ModelStreamEventKind.ReasoningStarted
                or ModelStreamEventKind.ToolCallStarted
                or ModelStreamEventKind.ToolCallDelta
                or ModelStreamEventKind.TextEnded
                or ModelStreamEventKind.ReasoningEnded
                or ModelStreamEventKind.ToolCallEnded)
            {
                content = partialContent;
            }

            if (modelEvent.Kind is ModelStreamEventKind.TextEnded or ModelStreamEventKind.ReasoningEnded)
            {
                var actual = partialContent switch
                {
                    TextContent text => text.Text,
                    ReasoningContent reasoning => reasoning.Text,
                    _ => throw new InvalidDataException("An upstream content-end event has the wrong content type."),
                };
                if (!string.Equals(modelEvent.Content, actual, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("An upstream content-end event disagrees with its partial response.");
                }
            }
            else if (modelEvent.Kind == ModelStreamEventKind.ToolCallEnded
                     && (modelEvent.ToolCall is null
                         || !ProxyWire.ContentEquals(modelEvent.ToolCall, partialContent)))
            {
                throw new InvalidDataException("An upstream tool-call end event disagrees with its partial response.");
            }
        }

        return new WireFrame
        {
            Type = ProxyWire.EventFrame,
            Kind = (int)modelEvent.Kind,
            ContentIndex = modelEvent.Kind == ModelStreamEventKind.Started ? null : modelEvent.ContentIndex,
            Delta = modelEvent.Delta,
            ToolCallId = modelEvent.ToolCallId,
            ToolName = modelEvent.ToolName,
            Content = content is null ? null : ProxyWire.Content(content),
        };
    }

    private static void ValidateRoundTrip(ModelStreamEvent expected, ModelStreamEvent? actual)
    {
        if (actual is null
            || expected.Kind != actual.Kind
            || expected.ContentIndex != actual.ContentIndex
            || !string.Equals(expected.Delta, actual.Delta, StringComparison.Ordinal)
            || !string.Equals(expected.Content, actual.Content, StringComparison.Ordinal)
            || !string.Equals(expected.ToolCallId, actual.ToolCallId, StringComparison.Ordinal)
            || !string.Equals(expected.ToolName, actual.ToolName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("An upstream model event cannot be represented by the proxy protocol.");
        }

        if (expected.ToolCall is not null
            && (actual.ToolCall is null || !ProxyWire.ContentEquals(expected.ToolCall, actual.ToolCall)))
        {
            throw new InvalidDataException("An upstream completed tool call cannot be represented by the proxy protocol.");
        }

        if (expected.Partial is not null
            && (actual.Partial is null
                || !ProxyWire.ContentSequenceEquals(expected.Partial.Content, actual.Partial.Content)))
        {
            throw new InvalidDataException("An upstream partial response cannot be reconstructed without loss.");
        }

        if (expected.Response is not null && actual.Response is not null)
        {
            ValidateResponseRoundTrip(expected.Response, actual.Response);
        }
    }

    private static void ValidateResponseRoundTrip(ModelResponse expected, ModelResponse actual)
    {
        if (!ProxyWire.ResponseEquals(expected, actual))
        {
            throw new InvalidDataException("An upstream terminal response cannot be represented without loss.");
        }
    }

    private static ModelResponse DefaultPartial(ModelRequest? request) => new(
        Array.Empty<AgentContent>(),
        ModelStopReason.Pending,
        responseModel: request?.Model);

    private static ModelResponse ErrorResponse(
        ModelResponse? partial,
        string message,
        ModelRequest? request)
    {
        var boundedMessage = string.IsNullOrWhiteSpace(message)
            ? "Remote model provider proxy failure."
            : message.Length <= 4_096 ? message : message.Substring(0, 4_096);
        return new ModelResponse(
            partial?.Content ?? Array.Empty<AgentContent>(),
            ModelStopReason.Error,
            partial?.Usage,
            boundedMessage,
            partial?.Provider,
            partial?.Api,
            partial?.ResponseModel ?? request?.Model,
            partial?.ResponseId,
            partial?.RawStopReason,
            partial?.EndTurn,
            partial?.Diagnostics);
    }

    private bool Authenticate(HttpRequestMessage request)
    {
        if (_settings.ApiKey is null)
        {
            return true;
        }

        if (!request.Headers.TryGetValues(_settings.ApiKeyHeader, out var values))
        {
            return false;
        }

        var supplied = values.SingleOrDefault();
        if (supplied is null)
        {
            return false;
        }

        var expected = string.IsNullOrEmpty(_settings.ApiKeyScheme)
            ? _settings.ApiKey
            : _settings.ApiKeyScheme + " " + _settings.ApiKey;
        return FixedTimeEquals(supplied, expected);
    }

    private async Task<string> ReadRequestBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0
            && content.Headers.ContentLength > _settings.MaximumRequestBytes)
        {
            throw new InvalidDataException("The remote provider request exceeded the configured size limit.");
        }

        using var source = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var registration = cancellationToken.Register(source.Dispose);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            int read;
            try
            {
                read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (read == 0)
            {
                break;
            }

            if (destination.Length + read > _settings.MaximumRequestBytes)
            {
                throw new InvalidDataException("The remote provider request exceeded the configured size limit.");
            }

            destination.Write(buffer, 0, read);
        }

        return StrictUtf8.GetString(destination.ToArray());
    }

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        var length = Math.Max(supplied.Length, expected.Length);
        var difference = supplied.Length ^ expected.Length;
        for (var index = 0; index < length; index++)
        {
            var left = index < supplied.Length ? supplied[index] : '\0';
            var right = index < expected.Length ? expected[index] : '\0';
            difference |= left ^ right;
        }

        return difference == 0;
    }

    private sealed class ServerSettings
    {
        private ServerSettings(ModelProviderProxyServerOptions options)
        {
            ApiKey = options.ApiKey;
            ApiKeyHeader = options.ApiKeyHeader;
            ApiKeyScheme = options.ApiKeyScheme;
            MaximumRequestBytes = options.MaximumRequestBytes;
            MaximumResponseBytes = options.MaximumResponseBytes;
            MaximumEventBytes = options.MaximumEventBytes;
            MaximumEvents = options.MaximumEvents;
            MaximumJsonDepth = options.MaximumJsonDepth;
        }

        public string? ApiKey { get; }
        public string ApiKeyHeader { get; }
        public string ApiKeyScheme { get; }
        public int MaximumRequestBytes { get; }
        public int MaximumResponseBytes { get; }
        public int MaximumEventBytes { get; }
        public int MaximumEvents { get; }
        public int MaximumJsonDepth { get; }

        public static ServerSettings Validate(ModelProviderProxyServerOptions options)
        {
            if (options.MaximumRequestBytes < 2 || options.MaximumRequestBytes > 100_000_000
                || options.MaximumResponseBytes < 2 || options.MaximumResponseBytes > 100_000_000
                || options.MaximumEventBytes < 2 || options.MaximumEventBytes > 100_000_000
                || options.MaximumEvents < 2 || options.MaximumEvents > 10_000_000
                || options.MaximumJsonDepth < 1 || options.MaximumJsonDepth > 1_024)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Remote provider proxy limits are invalid.");
            }

            RemoteModelProviderOptions.ValidateCredential(options.ApiKey, nameof(options.ApiKey), 65_536);
            RemoteModelProviderOptions.ValidateCredential(options.ApiKeyHeader, nameof(options.ApiKeyHeader), 256);
            RemoteModelProviderOptions.ValidateCredential(
                options.ApiKeyScheme,
                nameof(options.ApiKeyScheme),
                256,
                allowEmpty: true);
            if (!RemoteModelProviderOptions.IsValidHeaderName(options.ApiKeyHeader))
            {
                throw new ArgumentException("A valid server API key header name is required.", nameof(options));
            }

            return new ServerSettings(options);
        }
    }
}

internal sealed class ProxySseWriter
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly Stream _output;
    private readonly int _maximumEventBytes;
    private readonly int _maximumResponseBytes;
    private int _written;

    public ProxySseWriter(Stream output, int maximumEventBytes, int maximumResponseBytes)
    {
        _output = output;
        _maximumEventBytes = maximumEventBytes;
        _maximumResponseBytes = maximumResponseBytes;
    }

    public async Task WriteAsync(WireFrame frame, CancellationToken cancellationToken)
    {
        var json = ProxyWire.SerializeFrame(frame);
        var payload = Utf8.GetBytes("data:" + json + "\n\n");
        if (payload.Length > _maximumEventBytes)
        {
            throw new InvalidDataException("A remote provider proxy event exceeded the configured size limit.");
        }

        _written = checked(_written + payload.Length);
        if (_written > _maximumResponseBytes)
        {
            throw new InvalidDataException("The remote provider proxy response exceeded the configured size limit.");
        }

        await _output.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class ProxySseContent : HttpContent
{
    private readonly Func<Stream, CancellationToken, Task> _write;
    private readonly CancellationTokenSource _cancellation;
    private int _disposed;

    public ProxySseContent(
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        _write = write;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Headers.ContentType = new MediaTypeHeaderValue("text/event-stream")
        {
            CharSet = "utf-8",
        };
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        _write(stream, _cancellation.Token);

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (AggregateException)
            {
                // A third-party provider must not make HttpContent.Dispose fail by
                // throwing from a cancellation callback. Cancel invokes every
                // callback before aggregating, so it is safe to observe and ignore.
            }
            finally
            {
                _cancellation.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }
}
