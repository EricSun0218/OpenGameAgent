using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;
using OpenGameAgent.ProviderTransport;

namespace OpenGameAgent.Providers.MessageGateway;

public sealed class MessageGatewayProvider : IModelProvider, IModelProviderCapabilities
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly MessageGatewaySettings _settings;
    private readonly IReadOnlyCollection<string> _supportedApis;

    public MessageGatewayProvider(MessageGatewayProviderOptions options)
    {
        _settings = new MessageGatewaySettings(options ?? throw new ArgumentNullException(nameof(options)));
        _supportedApis = Array.AsReadOnly(new[] { _settings.ApiId });
    }

    public IReadOnlyCollection<string> SupportedApis => _supportedApis;

    public bool SupportsNativeDeferredTools => false;

    public bool SupportsDeferredResponses => false;

    public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        CancellationToken cancellationToken) =>
        StreamWithBoundaryAsync(
            request ?? throw new ArgumentNullException(nameof(request)),
            cancellationToken);

    private async IAsyncEnumerable<ModelStreamEvent> StreamWithBoundaryAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IAsyncEnumerator<ModelStreamEvent>? enumerator = null;
        Exception? setupError = null;
        try
        {
            enumerator = StreamCoreAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            setupError = exception;
        }

        if (setupError is not null)
        {
            yield return Failure(request, setupError);
            yield break;
        }

        var terminal = false;
        try
        {
            while (!terminal)
            {
                bool moved = false;
                ModelStreamEvent? current = null;
                Exception? moveError = null;
                try
                {
                    moved = await enumerator!.MoveNextAsync().ConfigureAwait(false);
                    if (moved)
                    {
                        current = enumerator.Current;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    moveError = exception;
                }

                if (moveError is not null)
                {
                    if (moveError is ModelProviderException)
                    {
                        throw moveError;
                    }

                    terminal = true;
                    yield return Failure(request, moveError);
                    continue;
                }

                if (!moved)
                {
                    terminal = true;
                    yield return Failure(
                        request,
                        new InvalidDataException("The message gateway stream ended without a terminal event."));
                    continue;
                }

                if (current is null)
                {
                    terminal = true;
                    yield return Failure(
                        request,
                        new InvalidDataException("The message gateway emitted a null event."));
                    continue;
                }

                terminal = current.IsTerminal;
                yield return current;
            }
        }
        finally
        {
            try
            {
                await enumerator!.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Cleanup must not replace a terminal result or caller cancellation.
            }
        }
    }

    private async IAsyncEnumerable<ModelStreamEvent> StreamCoreAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Parameters.Transport is ModelTransport.WebSocket or ModelTransport.CachedWebSocket)
        {
            throw new NotSupportedException("The message gateway uses a server-sent-event transport.");
        }

        var projected = MessageGatewayWire.ProjectRequest(request, _settings);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            RequestEndpoint(_settings.Endpoint, projected.Debug));
        foreach (var pair in _settings.Headers)
        {
            if (!httpRequest.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
            {
                throw new InvalidOperationException("A configured message gateway header could not be applied.");
            }
        }

        string? resolvedAccessToken = null;
        if (!httpRequest.Headers.Contains("Authorization"))
        {
            resolvedAccessToken = _settings.GetAccessTokenAsync is null
                ? _settings.AccessToken
                : await AwaitWithCancellation(
                    _settings.GetAccessTokenAsync(cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            MessageGatewaySettings.ValidateCredential(
                resolvedAccessToken,
                nameof(MessageGatewayProviderOptions.AccessToken));
            if (string.IsNullOrEmpty(resolvedAccessToken))
            {
                throw new InvalidOperationException("No access token is configured for the message gateway.");
            }

            if (!httpRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + resolvedAccessToken))
            {
                throw new InvalidOperationException("The message gateway authorization header could not be applied.");
            }
        }

        var redactor = new MessageGatewaySecretRedactor(_settings, resolvedAccessToken);

        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        httpRequest.Content = new ByteArrayContent(projected.Payload);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };

        HttpResponseMessage response;
        try
        {
            response = await AwaitOwnedWithCancellation(
                    _settings.HttpClient.SendAsync(
                        httpRequest,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            throw CreateTransportFailure(exception, redactor);
        }

        using (response)
        {
            var observation = RedactedObservation(request, response, redactor);
            await ProviderResponseObserverRunner.NotifyAsync(
                    _settings.ResponseObserver,
                    observation,
                    _settings.ResponseObserverTimeoutMilliseconds,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string body;
                try
                {
                    body = await ReadBoundedTextAsync(
                        response.Content,
                        _settings.MaxErrorCharacters,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    body = string.Empty;
                }

                throw CreateHttpFailure(response, observation, body, redactor);
            }

            if (!string.Equals(
                    response.Content.Headers.ContentType?.MediaType,
                    "text/event-stream",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The message gateway response must use text/event-stream.");
            }

            var streamTask = response.Content.ReadAsStreamAsync();
            using var pendingRegistration = cancellationToken.Register(response.Content.Dispose);
            using var responseStream = await AwaitOwnedWithCancellation(streamTask, cancellationToken).ConfigureAwait(false);
            using var cancellationRegistration = cancellationToken.Register(responseStream.Dispose);
            using var boundedStream = new BoundedReadStream(responseStream, _settings.MaxResponseBytes);
            using var reader = new StreamReader(boundedStream, StrictUtf8, false, 4096, leaveOpen: false);
            var state = new MessageGatewayStreamState(request, _settings, redactor);
            await foreach (var frame in ReadFramesAsync(reader, cancellationToken))
            {
                if (frame.Length == 0 || string.Equals(frame, "[DONE]", StringComparison.Ordinal))
                {
                    continue;
                }

                var decoded = state.Apply(frame);
                if (decoded.IsTerminal)
                {
                    yield return decoded;
                    yield break;
                }

                yield return decoded;
            }

            cancellationToken.ThrowIfCancellationRequested();
            state.EnsureComplete();
            throw new InvalidDataException("The message gateway stream ended without a terminal event.");
        }
    }

    private async IAsyncEnumerable<string> ReadFramesAsync(
        StreamReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var data = new StringBuilder();
        var dataBytes = 0;
        var hasData = false;
        var eventCount = 0;
        await foreach (var line in ReadLinesAsync(reader, cancellationToken))
        {
            if (line.Length == 0)
            {
                if (!hasData)
                {
                    continue;
                }

                eventCount++;
                if (eventCount > _settings.MaxEvents)
                {
                    throw new InvalidDataException("The message gateway exceeded its event-count limit.");
                }

                yield return data.ToString();
                data.Clear();
                dataBytes = 0;
                hasData = false;
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line.Substring(5);
                if (value.Length > 0 && value[0] == ' ')
                {
                    value = value.Substring(1);
                }

                var valueBytes = StrictUtf8.GetByteCount(value);
                var separatorBytes = hasData ? 1 : 0;
                if ((long)dataBytes + separatorBytes + valueBytes > _settings.MaxEventBytes)
                {
                    throw new InvalidDataException("A message gateway event exceeded its size limit.");
                }

                if (hasData)
                {
                    data.Append('\n');
                }

                data.Append(value);
                dataBytes += separatorBytes + valueBytes;
                hasData = true;
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal)
                || line.StartsWith("id:", StringComparison.Ordinal)
                || line.StartsWith("retry:", StringComparison.Ordinal))
            {
                continue;
            }

            throw new InvalidDataException("The message gateway stream contains an unsupported SSE field.");
        }

        if (hasData)
        {
            eventCount++;
            if (eventCount > _settings.MaxEvents)
            {
                throw new InvalidDataException("The message gateway exceeded its event-count limit.");
            }

            yield return data.ToString();
        }
    }

    private async IAsyncEnumerable<string> ReadLinesAsync(
        StreamReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new char[Math.Min(4096, _settings.MaxEventBytes + 1)];
        var line = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read;
            try
            {
                read = await AwaitWithCancellation(
                        reader.ReadAsync(buffer, 0, buffer.Length),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (read == 0)
            {
                if (line.Length > 0)
                {
                    yield return TrimCarriageReturn(line);
                }

                yield break;
            }

            for (var index = 0; index < read; index++)
            {
                if (buffer[index] == '\n')
                {
                    yield return TrimCarriageReturn(line);
                    line.Clear();
                }
                else
                {
                    line.Append(buffer[index]);
                    if (line.Length > _settings.MaxEventBytes)
                    {
                        throw new InvalidDataException("A message gateway SSE line exceeded its size limit.");
                    }
                }
            }
        }
    }

    private static string TrimCarriageReturn(StringBuilder line)
    {
        var length = line.Length;
        if (length > 0 && line[length - 1] == '\r')
        {
            length--;
        }

        return line.ToString(0, length);
    }

    private static Uri RequestEndpoint(Uri endpoint, bool debug)
    {
        if (!debug)
        {
            return endpoint;
        }

        var builder = new UriBuilder(endpoint);
        var pairs = builder.Query.TrimStart('?')
            .Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !string.Equals(
                Uri.UnescapeDataString(pair.Split(new[] { '=' }, 2)[0]),
                "debug",
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        pairs.Add("debug=1");
        builder.Query = string.Join("&", pairs);
        return builder.Uri;
    }

    private ModelProviderException CreateHttpFailure(
        HttpResponseMessage response,
        ProviderResponseObservation observation,
        string body,
        MessageGatewaySecretRedactor redactor)
    {
        var code = default(string);
        var detail = default(string);
        var structuredBody = LooksLikeJson(body);
        try
        {
            using var document = MessageGatewayJson.Parse(body, _settings.MaxJsonDepth);
            structuredBody = true;
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                code = OptionalErrorString(error, "code", 256);
                detail = OptionalErrorString(error, "message", _settings.MaxErrorCharacters);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            // A non-JSON error body remains available only as bounded, sanitized text.
        }

        code = code is null ? null : redactor.Sanitize(code, 256);
        var boundedBody = redactor.Sanitize(
            detail ?? (structuredBody ? string.Empty : body),
            _settings.MaxErrorCharacters);
        var status = (int)response.StatusCode;
        var message = $"The message gateway returned HTTP {status} ({redactor.Sanitize(response.ReasonPhrase ?? "error", 256)}).";
        if (boundedBody.Length > 0)
        {
            message += " " + boundedBody;
        }

        if (!string.IsNullOrEmpty(code))
        {
            message += " (" + code + ")";
        }

        var diagnosticData = JsonSerializer.Serialize(new
        {
            version = 1,
            statusCode = status,
            code,
            metadata = observation.Metadata,
        });
        var retry = ProviderHttpRetryMetadata.FromResponse(response);
        return new ModelProviderException(
            message,
            new[]
            {
                new ModelDiagnostic(
                    "message_gateway_response_failure",
                    "The message gateway returned an unsuccessful HTTP response.",
                    ModelDiagnosticSeverity.Error,
                    diagnosticData),
            },
            retry.IsTransient,
            retry.RetryAfter,
            status);
    }

    private ModelStreamEvent Failure(ModelRequest request, Exception exception)
    {
        var redactor = new MessageGatewaySecretRedactor(_settings, _settings.AccessToken);
        var message = redactor.Sanitize(
            string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message,
            4096);
        var diagnostics = exception is ModelProviderException providerException
                          && providerException.Diagnostics.Count > 0
            ? providerException.Diagnostics
            : new[]
            {
                new ModelDiagnostic(
                    "message_gateway_error",
                    message,
                    ModelDiagnosticSeverity.Error),
            };
        return ModelStreamEvent.Terminal(new ModelResponse(
            Array.Empty<AgentContent>(),
            ModelStopReason.Error,
            errorMessage: message,
            provider: _settings.ProviderId,
            api: _settings.ApiId,
            responseModel: request.Model,
            diagnostics: diagnostics));
    }

    private ModelProviderException CreateTransportFailure(
        Exception exception,
        MessageGatewaySecretRedactor redactor)
    {
        var detail = redactor.Sanitize(
            string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message,
            1_024);
        var message = "The message gateway transport failed.";
        if (detail.Length > 0)
        {
            message += " " + detail;
        }

        return new ModelProviderException(
            message,
            new[]
            {
                new ModelDiagnostic(
                    "message_gateway_transport_failure",
                    "The message gateway transport failed before a response was received.",
                    ModelDiagnosticSeverity.Error),
            },
            isTransient: true,
            innerException: exception);
    }

    private ProviderResponseObservation RedactedObservation(
        ModelRequest request,
        HttpResponseMessage response,
        MessageGatewaySecretRedactor redactor)
    {
        var observation = ProviderResponseObservation.FromHttpResponse(
            _settings.ProviderId,
            _settings.ApiId,
            request.Model,
            response);
        var metadata = observation.Metadata.ToDictionary(
            pair => pair.Key,
            pair => redactor.Sanitize(pair.Value, 1_024),
            StringComparer.OrdinalIgnoreCase);
        return ProviderResponseObservation.FromResponseMetadata(
            _settings.ProviderId,
            _settings.ApiId,
            request.Model,
            observation.StatusCode,
            metadata);
    }

    private static string? OptionalErrorString(JsonElement value, string property, int maximumCharacters)
    {
        if (!value.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.String
            || element.GetString() is not { } result
            || result.Length > maximumCharacters)
        {
            return null;
        }

        return result;
    }

    private static bool LooksLikeJson(string value)
    {
        var trimmed = value.TrimStart();
        return trimmed.StartsWith("{", StringComparison.Ordinal)
               || trimmed.StartsWith("[", StringComparison.Ordinal)
               || trimmed.StartsWith("\"", StringComparison.Ordinal);
    }

    private static async Task<string> ReadBoundedTextAsync(
        HttpContent content,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        using var source = await AwaitOwnedWithCancellation(content.ReadAsStreamAsync(), cancellationToken)
            .ConfigureAwait(false);
        using var cancellationRegistration = cancellationToken.Register(source.Dispose);
        using var bounded = new BoundedReadStream(
            source,
            Math.Min(100_000_000L, Math.Max(4L, maximumCharacters * 4L)));
        using var reader = new StreamReader(bounded, StrictUtf8, false, 4096, leaveOpen: false);
        var buffer = new char[Math.Min(4096, maximumCharacters)];
        var result = new StringBuilder();
        while (result.Length < maximumCharacters)
        {
            var read = await AwaitWithCancellation(
                    reader.ReadAsync(
                        buffer,
                        0,
                        Math.Min(buffer.Length, maximumCharacters - result.Length)),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            result.Append(buffer, 0, read);
        }

        return result.ToString();
    }

    private static async ValueTask<T> AwaitWithCancellation<T>(
        ValueTask<T> operation,
        CancellationToken cancellationToken) =>
        await AwaitWithCancellation(operation.AsTask(), cancellationToken).ConfigureAwait(false);

    private static async Task<T> AwaitWithCancellation<T>(
        Task<T> operation,
        CancellationToken cancellationToken)
    {
        if (operation.IsCompleted)
        {
            return await operation.ConfigureAwait(false);
        }

        var cancellation = Task.Delay(Timeout.Infinite, cancellationToken);
        if (await Task.WhenAny(operation, cancellation).ConfigureAwait(false) != operation)
        {
            Observe(operation);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return await operation.ConfigureAwait(false);
    }

    private static async Task<T> AwaitOwnedWithCancellation<T>(
        Task<T> operation,
        CancellationToken cancellationToken)
        where T : IDisposable
    {
        if (operation.IsCompleted)
        {
            return await operation.ConfigureAwait(false);
        }

        var cancellation = Task.Delay(Timeout.Infinite, cancellationToken);
        if (await Task.WhenAny(operation, cancellation).ConfigureAwait(false) != operation)
        {
            ObserveOwned(operation);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return await operation.ConfigureAwait(false);
    }

    private static void Observe(Task task)
    {
        _ = ObserveAsync(task);
    }

    private static void ObserveOwned<T>(Task<T> task)
        where T : IDisposable
    {
        _ = ObserveOwnedAsync(task);
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Detached completion cannot affect the canceled request.
        }
    }

    private static async Task ObserveOwnedAsync<T>(Task<T> task)
        where T : IDisposable
    {
        try
        {
            (await task.ConfigureAwait(false)).Dispose();
        }
        catch
        {
            // Detached completion cannot affect the canceled request.
        }
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maximumBytes;
        private long _bytesRead;

        public BoundedReadStream(Stream inner, long maximumBytes)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _maximumBytes = maximumBytes > 0
                ? maximumBytes
                : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, BoundedCount(count));
            Account(read);
            return read;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var read = await _inner.ReadAsync(buffer, offset, BoundedCount(count), cancellationToken)
                .ConfigureAwait(false);
            Account(read);
            return read;
        }

        private int BoundedCount(int requested)
        {
            var remaining = _maximumBytes - _bytesRead;
            return (int)Math.Min(requested, Math.Max(1L, remaining + 1L));
        }

        private void Account(int read)
        {
            _bytesRead += read;
            if (_bytesRead > _maximumBytes)
            {
                throw new InvalidDataException("The message gateway response exceeded its size limit.");
            }
        }
    }
}
