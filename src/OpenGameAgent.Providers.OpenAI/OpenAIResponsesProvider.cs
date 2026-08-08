using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;
using OpenGameAgent.ProviderTransport;

namespace OpenGameAgent.Providers.OpenAI;

public delegate ValueTask<string?> OpenAIApiKeyProvider(CancellationToken cancellationToken);

public enum OpenAISessionAffinityFormat
{
    OpenAI,
    OpenAIWithoutSessionHeader,
    OpenRouter,
    Codex,
}

public enum OpenAIAuthenticationStyle
{
    Bearer,
    ApiKeyHeader,
    None,
}

public enum OpenAISystemPromptMode
{
    InputMessage,
    Instructions,
}

public enum OpenAIToolChoice
{
    Auto,
    None,
    Required,
}

public enum OpenAITextVerbosity
{
    Low,
    Medium,
    High,
}

public sealed class OpenAIRequestCredential
{
    public OpenAIRequestCredential(
        string? apiKey,
        IReadOnlyDictionary<string, string?>? headers = null)
    {
        ApiKey = apiKey;
        Headers = new ReadOnlyDictionary<string, string?>(
            new Dictionary<string, string?>(
                headers ?? new Dictionary<string, string?>(),
                StringComparer.OrdinalIgnoreCase));
    }

    public string? ApiKey { get; }

    public IReadOnlyDictionary<string, string?> Headers { get; }
}

public delegate ValueTask<OpenAIRequestCredential?> OpenAIRequestCredentialProvider(
    CancellationToken cancellationToken);

public sealed class OpenAIResponsesProviderOptions
{
    public OpenAIResponsesProviderOptions(HttpClient httpClient, Uri endpoint)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public HttpClient HttpClient { get; }

    public Uri Endpoint { get; }

    public string? ApiKey { get; set; }

    public OpenAIApiKeyProvider? GetApiKeyAsync { get; set; }

    public OpenAIRequestCredentialProvider? GetCredentialAsync { get; set; }

    public OpenAIAuthenticationStyle AuthenticationStyle { get; set; } = OpenAIAuthenticationStyle.Bearer;

    public string ApiKeyHeaderName { get; set; } = "api-key";

    public OpenAISystemPromptMode SystemPromptMode { get; set; } = OpenAISystemPromptMode.InputMessage;

    public string DefaultInstructions { get; set; } = "You are a helpful assistant.";

    public string? ReasoningSummary { get; set; }

    public string? ServiceTier { get; set; }

    public OpenAITextVerbosity? TextVerbosity { get; set; }

    public OpenAIToolChoice? ToolChoice { get; set; }

    public bool? ParallelToolCalls { get; set; }

    public bool AlwaysIncludeEncryptedReasoning { get; set; }

    public IDictionary<string, string?> Headers { get; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    public ProviderResponseObserver? ResponseObserver { get; set; }

    public int ResponseObserverTimeoutMilliseconds { get; set; } =
        ProviderResponseObserverRunner.DefaultTimeoutMilliseconds;

    public string ProviderId { get; set; } = "openai";

    public string ApiId { get; set; } = "openai-responses";

    public bool AllowInsecureHttp { get; set; }

    public bool SupportsDeveloperRole { get; set; } = true;

    public bool SupportsStrictTools { get; set; }

    public bool SupportsGrammarTools { get; set; }

    public bool SupportsAdditionalTools { get; set; }

    public bool SupportsToolSearch { get; set; }

    public bool SupportsExplicitPromptCacheMode { get; set; }

    public bool SupportsLongCacheRetention { get; set; } = true;

    public bool SupportsWebSocketTransport { get; set; }

    public OpenAIWebSocketConnectionFactory? WebSocketConnectionFactory { get; set; }

    public int WebSocketIdleTimeoutMilliseconds { get; set; } = 300_000;

    public int WebSocketSessionIdleTimeoutMilliseconds { get; set; } = 300_000;

    public int WebSocketMaximumConnectionAgeMilliseconds { get; set; } = 3_300_000;

    public OpenAISessionAffinityFormat SessionAffinityFormat { get; set; } = OpenAISessionAffinityFormat.OpenAI;

    public int MaxEventCharacters { get; set; } = 4_000_000;

    public int MaxErrorCharacters { get; set; } = 64_000;

    public int MaxRequestBytes { get; set; } = 16_000_000;

    public int MaxResponseCharacters { get; set; } = 16_000_000;

    public int MaxToolCallsPerResponse { get; set; } = 256;
}

public sealed class OpenAIResponsesProvider : IModelProvider, IModelProviderCapabilities, IDisposable
{
    private const int MinimumOutputTokens = 16;
    private const string WebSocketBetaHeader = "responses_websockets=2026-02-06";
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly string? _apiKey;
    private readonly OpenAIApiKeyProvider? _getApiKeyAsync;
    private readonly OpenAIRequestCredentialProvider? _getCredentialAsync;
    private readonly OpenAIAuthenticationStyle _authenticationStyle;
    private readonly string _apiKeyHeaderName;
    private readonly OpenAISystemPromptMode _systemPromptMode;
    private readonly string _defaultInstructions;
    private readonly string? _reasoningSummary;
    private readonly string? _serviceTier;
    private readonly OpenAITextVerbosity? _textVerbosity;
    private readonly OpenAIToolChoice? _toolChoice;
    private readonly bool? _parallelToolCalls;
    private readonly bool _alwaysIncludeEncryptedReasoning;
    private readonly IReadOnlyDictionary<string, string?> _headers;
    private readonly ProviderResponseObserver? _responseObserver;
    private readonly int _responseObserverTimeoutMilliseconds;
    private readonly string _providerId;
    private readonly string _apiId;
    private readonly bool _supportsDeveloperRole;
    private readonly bool _supportsStrictTools;
    private readonly bool _supportsGrammarTools;
    private readonly bool _supportsAdditionalTools;
    private readonly bool _supportsToolSearch;
    private readonly bool _supportsExplicitPromptCacheMode;
    private readonly bool _supportsLongCacheRetention;
    private readonly OpenAIWebSocketConnectionFactory? _webSocketConnectionFactory;
    private readonly int _webSocketIdleTimeoutMilliseconds;
    private readonly int _webSocketSessionIdleTimeoutMilliseconds;
    private readonly int _webSocketMaximumConnectionAgeMilliseconds;
    private readonly OpenAISessionAffinityFormat _sessionAffinityFormat;
    private readonly int _maxEventCharacters;
    private readonly int _maxErrorCharacters;
    private readonly int _maxRequestBytes;
    private readonly int _maxResponseCharacters;
    private readonly int _maxToolCallsPerResponse;
    private readonly IReadOnlyCollection<string> _supportedApis;
    private readonly object _webSocketGate = new();
    private readonly Dictionary<string, Dictionary<string, CachedWebSocketConnection>> _webSocketSessions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, MutableWebSocketStatistics> _webSocketStatistics =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _webSocketFallbackSessions = new(StringComparer.Ordinal);
    private bool _disposed;

    public OpenAIResponsesProvider(OpenAIResponsesProviderOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        ValidateOptions(options);
        _httpClient = options.HttpClient;
        _endpoint = options.Endpoint;
        _apiKey = options.ApiKey;
        _getApiKeyAsync = options.GetApiKeyAsync;
        _getCredentialAsync = options.GetCredentialAsync;
        _authenticationStyle = options.AuthenticationStyle;
        _apiKeyHeaderName = options.ApiKeyHeaderName;
        _systemPromptMode = options.SystemPromptMode;
        _defaultInstructions = options.DefaultInstructions;
        _reasoningSummary = options.ReasoningSummary;
        _serviceTier = options.ServiceTier;
        _textVerbosity = options.TextVerbosity;
        _toolChoice = options.ToolChoice;
        _parallelToolCalls = options.ParallelToolCalls;
        _alwaysIncludeEncryptedReasoning = options.AlwaysIncludeEncryptedReasoning;
        _headers = new ReadOnlyDictionary<string, string?>(
            new Dictionary<string, string?>(options.Headers, StringComparer.OrdinalIgnoreCase));
        _responseObserver = options.ResponseObserver;
        _responseObserverTimeoutMilliseconds = options.ResponseObserverTimeoutMilliseconds;
        _providerId = options.ProviderId;
        _apiId = options.ApiId;
        _supportsDeveloperRole = options.SupportsDeveloperRole;
        _supportsStrictTools = options.SupportsStrictTools;
        _supportsGrammarTools = options.SupportsGrammarTools;
        _supportsAdditionalTools = options.SupportsAdditionalTools;
        _supportsToolSearch = options.SupportsToolSearch;
        _supportsExplicitPromptCacheMode = options.SupportsExplicitPromptCacheMode;
        _supportsLongCacheRetention = options.SupportsLongCacheRetention;
        _webSocketConnectionFactory = options.SupportsWebSocketTransport
            ? options.WebSocketConnectionFactory ?? ClientOpenAIWebSocketConnection.ConnectAsync
            : null;
        _webSocketIdleTimeoutMilliseconds = options.WebSocketIdleTimeoutMilliseconds;
        _webSocketSessionIdleTimeoutMilliseconds = options.WebSocketSessionIdleTimeoutMilliseconds;
        _webSocketMaximumConnectionAgeMilliseconds = options.WebSocketMaximumConnectionAgeMilliseconds;
        _sessionAffinityFormat = options.SessionAffinityFormat;
        _maxEventCharacters = options.MaxEventCharacters;
        _maxErrorCharacters = options.MaxErrorCharacters;
        _maxRequestBytes = options.MaxRequestBytes;
        _maxResponseCharacters = options.MaxResponseCharacters;
        _maxToolCallsPerResponse = options.MaxToolCallsPerResponse;
        _supportedApis = Array.AsReadOnly(new[] { _apiId });
    }

    public IReadOnlyCollection<string> SupportedApis => _supportedApis;

    public bool SupportsNativeDeferredTools => _supportsAdditionalTools || _supportsToolSearch;

    public bool SupportsDeferredResponses => false;

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ThrowIfDisposed();
        var credential = await ResolveCredentialAsync(cancellationToken).ConfigureAwait(false);
        var transport = request.Parameters.Transport;
        if (transport == ModelTransport.ServerSentEvents
            || transport == ModelTransport.Auto && _webSocketConnectionFactory is null)
        {
            await foreach (var streamEvent in StreamSseAsync(request, credential, null, cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return streamEvent;
            }

            yield break;
        }

        if (_webSocketConnectionFactory is null)
        {
            throw new NotSupportedException("This provider does not support the requested WebSocket transport.");
        }

        var cacheSessionId = CacheSessionId(request);
        if (cacheSessionId is not null && IsWebSocketFallbackActive(cacheSessionId))
        {
            RecordWebSocketSseFallback(cacheSessionId);
            var diagnostic = TransportDiagnostic(
                transport,
                "The session previously encountered a WebSocket transport failure and is using server-sent events.");
            await foreach (var streamEvent in StreamSseAsync(request, credential, diagnostic, cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return streamEvent;
            }

            yield break;
        }

        var webSocketStream = StreamWebSocketWithRecoveryAsync(request, credential, cancellationToken);
        var enumerator = webSocketStream.GetAsyncEnumerator(cancellationToken);
        var moved = false;
        ModelStreamEvent? current = null;
        Exception? failure = null;
        try
        {
            moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            if (moved)
            {
                current = enumerator.Current;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (failure is not null || !moved || current is null)
        {
            await DisposeIgnoringFailureAsync(enumerator).ConfigureAwait(false);
            failure ??= new InvalidDataException("The WebSocket stream ended before its first event.");
            if (!CanFallbackToServerSentEvents(failure))
            {
                throw failure;
            }

            RecordWebSocketFailure(cacheSessionId, failure);
            RecordWebSocketSseFallback(cacheSessionId);
            var diagnostic = TransportDiagnostic(transport, BoundExceptionMessage(failure));
            await foreach (var streamEvent in StreamSseAsync(request, credential, diagnostic, cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return streamEvent;
            }

            yield break;
        }

        try
        {
            while (true)
            {
                yield return current;
                if (current.IsTerminal)
                {
                    yield break;
                }

                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    throw new InvalidDataException("The WebSocket stream ended without a terminal response.");
                }

                current = enumerator.Current
                          ?? throw new InvalidDataException("The WebSocket provider emitted a null event.");
            }
        }
        finally
        {
            await DisposeIgnoringFailureAsync(enumerator).ConfigureAwait(false);
        }
    }

    private async IAsyncEnumerable<ModelStreamEvent> StreamSseAsync(
        ModelRequest request,
        OpenAIRequestCredential credential,
        ModelDiagnostic? transportDiagnostic,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        ApplyHeaders(httpRequest, credential, request);
        httpRequest.Content = new ByteArrayContent(SerializeRequest(request));
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };

        using var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await ProviderResponseObserverRunner.NotifyAsync(
                _responseObserver,
                ProviderResponseObservation.FromHttpResponse(
                    _providerId,
                    _apiId,
                    request.Model,
                    response),
                _responseObserverTimeoutMilliseconds,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadBoundedAsync(response.Content, _maxErrorCharacters, cancellationToken).ConfigureAwait(false);
            var retry = ProviderHttpRetryMetadata.FromResponse(response, errorText: error);
            throw new ModelProviderException(
                $"The Responses endpoint returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {error}",
                retry.IsTransient,
                retry.RetryAfter,
                (int)response.StatusCode);
        }

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var cancellationRegistration = cancellationToken.Register(stream.Dispose);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
        var state = new ResponsesStreamState(
            request.Model,
            _providerId,
            _apiId,
            GrammarInputProperties(request.Tools),
            _maxResponseCharacters,
            _maxToolCallsPerResponse);
        if (transportDiagnostic is not null)
        {
            state.AddDiagnostic(transportDiagnostic);
        }

        yield return ModelStreamEvent.Update(ModelStreamEventKind.Started, state.Partial());

        await foreach (var line in ReadBoundedLinesAsync(reader, _maxEventCharacters, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line.Substring(5).TrimStart();
            if (data.Length == 0 || data == "[DONE]")
            {
                continue;
            }

            foreach (var item in state.Apply(data))
            {
                yield return item;
            }

            if (state.IsTerminal)
            {
                break;
            }
        }

        yield return ModelStreamEvent.Terminal(state.Complete());
    }

    private async IAsyncEnumerable<ModelStreamEvent> StreamWebSocketWithRecoveryAsync(
        ModelRequest request,
        OpenAIRequestCredential credential,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var retriedConnectionLimit = false;
        var retriedMissingContinuation = false;
        var forceFullContext = false;
        while (true)
        {
            var attempt = StreamWebSocketAttemptAsync(
                request,
                credential,
                forceFullContext,
                cancellationToken);
            var enumerator = attempt.GetAsyncEnumerator(cancellationToken);
            var moved = false;
            ModelStreamEvent? current = null;
            Exception? failure = null;
            try
            {
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                if (moved)
                {
                    current = enumerator.Current;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await DisposeIgnoringFailureAsync(enumerator).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            if (failure is OpenAIWebSocketProtocolException protocolFailure
                && string.Equals(
                    protocolFailure.Code,
                    "previous_response_not_found",
                    StringComparison.OrdinalIgnoreCase)
                && !retriedMissingContinuation)
            {
                retriedMissingContinuation = true;
                forceFullContext = true;
                await DisposeIgnoringFailureAsync(enumerator).ConfigureAwait(false);
                continue;
            }

            if (failure is OpenAIWebSocketProtocolException limitFailure
                && string.Equals(
                    limitFailure.Code,
                    "websocket_connection_limit_reached",
                    StringComparison.OrdinalIgnoreCase)
                && !retriedConnectionLimit)
            {
                retriedConnectionLimit = true;
                await DisposeIgnoringFailureAsync(enumerator).ConfigureAwait(false);
                continue;
            }

            if (failure is not null)
            {
                await DisposeIgnoringFailureAsync(enumerator).ConfigureAwait(false);
                throw failure;
            }

            if (!moved || current is null)
            {
                await DisposeIgnoringFailureAsync(enumerator).ConfigureAwait(false);
                throw new InvalidDataException("The WebSocket attempt ended before its first event.");
            }

            try
            {
                while (true)
                {
                    yield return current;
                    if (current.IsTerminal)
                    {
                        yield break;
                    }

                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        throw new InvalidDataException(
                            "The WebSocket attempt ended without a terminal response.");
                    }

                    current = enumerator.Current
                              ?? throw new InvalidDataException("The WebSocket attempt emitted a null event.");
                }
            }
            finally
            {
                await DisposeIgnoringFailureAsync(enumerator).ConfigureAwait(false);
            }
        }
    }

    private async IAsyncEnumerable<ModelStreamEvent> StreamWebSocketAttemptAsync(
        ModelRequest request,
        OpenAIRequestCredential credential,
        bool forceFullContext,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cacheSessionId = CacheSessionId(request);
        var accountId = AccountId(credential);
        var lease = await AcquireWebSocketAsync(
                request,
                credential,
                cacheSessionId,
                accountId,
                cancellationToken)
            .ConfigureAwait(false);
        var completed = false;
        try
        {
            var body = SerializeRequest(request);
            var bodySnapshot = RequestBodySnapshot.Create(body);
            var useCachedContext = request.Parameters.Transport is ModelTransport.Auto or ModelTransport.CachedWebSocket;
            RequestBodyDelta? delta = null;
            if (useCachedContext && !forceFullContext && lease.Entry?.Continuation is { } continuation)
            {
                delta = bodySnapshot.TryCreateDelta(continuation);
                if (delta is null)
                {
                    lease.Entry.Continuation = null;
                }
            }

            var requestJson = bodySnapshot.CreateWebSocketRequest(delta);
            if (Encoding.UTF8.GetByteCount(requestJson) > _maxRequestBytes)
            {
                throw new InvalidDataException("The WebSocket request exceeded the configured byte limit.");
            }

            var state = new ResponsesStreamState(
                request.Model,
                _providerId,
                _apiId,
                GrammarInputProperties(request.Tools),
                _maxResponseCharacters,
                _maxToolCallsPerResponse);
            RecordWebSocketRequest(cacheSessionId, lease.Reused, delta is not null);
            await AwaitWithCancellationAsync(
                    lease.Connection.SendTextAsync(requestJson, cancellationToken).AsTask(),
                    cancellationToken)
                .ConfigureAwait(false);
            var started = false;
            while (!state.IsTerminal)
            {
                var json = await ReceiveWebSocketEventAsync(lease.Connection, cancellationToken)
                    .ConfigureAwait(false);
                ThrowIfWebSocketProtocolError(json);
                var updates = state.Apply(json);
                if (!started && (updates.Count > 0 || state.IsTerminal))
                {
                    started = true;
                    yield return ModelStreamEvent.Update(ModelStreamEventKind.Started, state.Partial());
                }

                foreach (var update in updates)
                {
                    yield return update;
                }
            }

            var response = state.Complete();
            if (!started)
            {
                yield return ModelStreamEvent.Update(ModelStreamEventKind.Started, state.Partial());
            }

            if (useCachedContext && lease.Entry is not null && response.ResponseId is { } responseId)
            {
                lease.Entry.Continuation = new WebSocketContinuation(
                    bodySnapshot.Fingerprint,
                    responseId,
                    bodySnapshot.InputItems.Concat(ProjectResponseItems(request, response)).ToArray());
            }

            completed = true;
            yield return ModelStreamEvent.Terminal(response);
        }
        finally
        {
            if (!completed && lease.Entry is not null)
            {
                lease.Entry.Continuation = null;
            }

            lease.Release(completed);
        }
    }

    private async ValueTask<string> ReceiveWebSocketEventAsync(
        IOpenAIWebSocketConnection connection,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_webSocketIdleTimeoutMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await AwaitWithCancellationAsync(
                    connection.ReceiveTextAsync(_maxEventCharacters, linked.Token).AsTask(),
                    linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"WebSocket idle timeout after {_webSocketIdleTimeoutMilliseconds}ms.",
                exception);
        }
    }

    private async ValueTask<WebSocketLease> AcquireWebSocketAsync(
        ModelRequest request,
        OpenAIRequestCredential credential,
        string? sessionId,
        string accountId,
        CancellationToken cancellationToken)
    {
        CachedWebSocketConnection? reusable = null;
        CachedWebSocketConnection? stale = null;
        if (sessionId is not null)
        {
            lock (_webSocketGate)
            {
                if (_webSocketSessions.TryGetValue(sessionId, out var accounts)
                    && accounts.TryGetValue(accountId, out var cached))
                {
                    if (!cached.Busy
                        && (DateTimeOffset.UtcNow - cached.CreatedAt).TotalMilliseconds
                        >= _webSocketMaximumConnectionAgeMilliseconds)
                    {
                        accounts.Remove(accountId);
                        if (accounts.Count == 0)
                        {
                            _webSocketSessions.Remove(sessionId);
                        }

                        stale = cached;
                    }
                    else if (!cached.Busy && cached.Connection.IsOpen)
                    {
                        cached.Busy = true;
                        cached.IdleTimer?.Dispose();
                        cached.IdleTimer = null;
                        reusable = cached;
                    }
                    else if (!cached.Busy && !cached.Connection.IsOpen)
                    {
                        accounts.Remove(accountId);
                        if (accounts.Count == 0)
                        {
                            _webSocketSessions.Remove(sessionId);
                        }

                        stale = cached;
                    }
                }
            }
        }

        stale?.Dispose();
        if (reusable is not null)
        {
            return new WebSocketLease(
                reusable.Connection,
                reusable,
                reused: true,
                keep => ReleaseCachedWebSocket(sessionId!, accountId, reusable, keep));
        }

        var headers = BuildWebSocketHeaders(credential, request);
        var endpoint = WebSocketEndpoint(_endpoint);
        var connectRequest = new OpenAIWebSocketConnectRequest(
            endpoint,
            headers,
            request.Parameters.WebSocketConnectTimeoutMilliseconds);
        using var connectTimeout = connectRequest.TimeoutMilliseconds is { } connectMilliseconds
            ? new CancellationTokenSource(connectMilliseconds)
            : null;
        using var connectCancellation = connectTimeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectTimeout.Token);
        var connectToken = connectCancellation?.Token ?? cancellationToken;
        var connectTask = _webSocketConnectionFactory!(connectRequest, connectToken).AsTask();
        IOpenAIWebSocketConnection connection = null!;
        try
        {
            connection = await AwaitWithCancellationAsync(
                    connectTask,
                    connectToken,
                    lateResult => lateResult?.Dispose())
                .ConfigureAwait(false)
                         ?? throw new InvalidOperationException("The WebSocket connection factory returned null.");

            if (connection is IOpenAIWebSocketResponseMetadata metadata)
            {
                await ProviderResponseObserverRunner.NotifyAsync(
                        _responseObserver,
                        ProviderResponseObservation.FromResponseMetadata(
                            _providerId,
                            _apiId,
                            request.Model,
                            metadata.HandshakeStatusCode,
                            metadata.HandshakeHeaders),
                        _responseObserverTimeoutMilliseconds,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (
            connectTimeout?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"WebSocket connect timeout after {connectRequest.TimeoutMilliseconds}ms.",
                exception);
        }
        catch
        {
            connection?.Dispose();
            throw;
        }

        if (sessionId is null)
        {
            return new WebSocketLease(
                connection,
                entry: null,
                reused: false,
                _ => connection.Dispose());
        }

        CachedWebSocketConnection? entry = null;
        lock (_webSocketGate)
        {
            if (!_webSocketSessions.TryGetValue(sessionId, out var accounts))
            {
                accounts = new Dictionary<string, CachedWebSocketConnection>(StringComparer.Ordinal);
                _webSocketSessions[sessionId] = accounts;
            }

            if (!accounts.ContainsKey(accountId))
            {
                entry = new CachedWebSocketConnection(connection);
                accounts[accountId] = entry;
            }
        }

        if (entry is null)
        {
            return new WebSocketLease(
                connection,
                entry: null,
                reused: false,
                _ => connection.Dispose());
        }

        return new WebSocketLease(
            connection,
            entry,
            reused: false,
            keep => ReleaseCachedWebSocket(sessionId, accountId, entry, keep));
    }

    private void ReleaseCachedWebSocket(
        string sessionId,
        string accountId,
        CachedWebSocketConnection entry,
        bool keep)
    {
        var dispose = false;
        lock (_webSocketGate)
        {
            if (!_webSocketSessions.TryGetValue(sessionId, out var accounts)
                || !ReferenceEquals(accounts.GetValueOrDefault(accountId), entry))
            {
                dispose = true;
            }
            else if (!keep || !entry.Connection.IsOpen)
            {
                accounts.Remove(accountId);
                if (accounts.Count == 0)
                {
                    _webSocketSessions.Remove(sessionId);
                }

                dispose = true;
            }
            else
            {
                entry.Busy = false;
                entry.IdleTimer?.Dispose();
                entry.IdleTimer = new Timer(
                    _ => ExpireWebSocket(sessionId, accountId, entry),
                    null,
                    _webSocketSessionIdleTimeoutMilliseconds,
                    Timeout.Infinite);
            }
        }

        if (dispose)
        {
            entry.Dispose();
        }
    }

    private void ExpireWebSocket(string sessionId, string accountId, CachedWebSocketConnection entry)
    {
        var dispose = false;
        lock (_webSocketGate)
        {
            if (!entry.Busy
                && _webSocketSessions.TryGetValue(sessionId, out var accounts)
                && ReferenceEquals(accounts.GetValueOrDefault(accountId), entry))
            {
                accounts.Remove(accountId);
                if (accounts.Count == 0)
                {
                    _webSocketSessions.Remove(sessionId);
                }

                dispose = true;
            }
        }

        if (dispose)
        {
            entry.Dispose();
        }
    }

    public OpenAIWebSocketStatistics? GetWebSocketStatistics(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A session identifier is required.", nameof(sessionId));
        }

        lock (_webSocketGate)
        {
            return _webSocketStatistics.TryGetValue(sessionId, out var value)
                ? value.Snapshot(_webSocketFallbackSessions.Contains(sessionId))
                : null;
        }
    }

    public void ResetWebSocketStatistics(string? sessionId = null)
    {
        lock (_webSocketGate)
        {
            if (sessionId is null)
            {
                _webSocketStatistics.Clear();
                _webSocketFallbackSessions.Clear();
                return;
            }

            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new ArgumentException("A session identifier cannot be empty.", nameof(sessionId));
            }

            _webSocketStatistics.Remove(sessionId);
            _webSocketFallbackSessions.Remove(sessionId);
        }
    }

    public void CloseWebSocketSessions(string? sessionId = null)
    {
        List<CachedWebSocketConnection> entries;
        lock (_webSocketGate)
        {
            if (sessionId is not null)
            {
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    throw new ArgumentException("A session identifier cannot be empty.", nameof(sessionId));
                }

                entries = _webSocketSessions.TryGetValue(sessionId, out var accounts)
                    ? accounts.Values.ToList()
                    : new List<CachedWebSocketConnection>();
                _webSocketSessions.Remove(sessionId);
                _webSocketFallbackSessions.Remove(sessionId);
            }
            else
            {
                entries = _webSocketSessions.Values.SelectMany(accounts => accounts.Values).ToList();
                _webSocketSessions.Clear();
                _webSocketFallbackSessions.Clear();
            }
        }

        foreach (var entry in entries)
        {
            entry.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseWebSocketSessions();
    }

    private async ValueTask<OpenAIRequestCredential> ResolveCredentialAsync(
        CancellationToken cancellationToken)
    {
        if (_getCredentialAsync is not null)
        {
            return await ProviderCallbackRunner.RunAsync(
                       token => _getCredentialAsync(token),
                       cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("The credential provider returned null.");
        }

        var apiKey = _getApiKeyAsync is null
            ? _apiKey
            : await ProviderCallbackRunner.RunAsync(
                    token => _getApiKeyAsync(token),
                    cancellationToken)
                .ConfigureAwait(false);
        return new OpenAIRequestCredential(apiKey);
    }

    private IReadOnlyDictionary<string, string> BuildWebSocketHeaders(
        OpenAIRequestCredential credential,
        ModelRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        ApplyHeaders(message, credential, request);
        var headers = message.Headers.ToDictionary(
            header => header.Key,
            header => string.Join(",", header.Value),
            StringComparer.OrdinalIgnoreCase);
        headers.Remove("Accept");
        headers.Remove("Content-Type");
        headers.Remove("OpenAI-Beta");
        headers["OpenAI-Beta"] = WebSocketBetaHeader;
        var requestId = request.Parameters.CacheRetention == ModelCacheRetention.None
            ? Guid.NewGuid().ToString("N")
            : request.SessionId is { Length: > 0 } sessionId
                ? ClampUnicode(sessionId, 64)
                : Guid.NewGuid().ToString("N");
        headers["session-id"] = requestId;
        headers["x-client-request-id"] = requestId;
        return new ReadOnlyDictionary<string, string>(headers);
    }

    private static Uri WebSocketEndpoint(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint)
        {
            Scheme = endpoint.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Port = endpoint.IsDefaultPort ? -1 : endpoint.Port,
        };
        return builder.Uri;
    }

    private static string AccountId(OpenAIRequestCredential credential)
    {
        if (credential.Headers.TryGetValue("chatgpt-account-id", out var accountId)
            && !string.IsNullOrWhiteSpace(accountId))
        {
            return accountId;
        }

        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(credential.ApiKey ?? string.Empty));
        return Convert.ToBase64String(bytes);
    }

    private static string? CacheSessionId(ModelRequest request) =>
        request.Parameters.CacheRetention == ModelCacheRetention.None
            ? null
            : request.SessionId;

    private IReadOnlyList<string> ProjectResponseItems(ModelRequest request, ModelResponse response)
    {
        var assistant = new AgentMessage(
            AgentRole.Assistant,
            response.Content,
            DateTimeOffset.UtcNow,
            model: request.Model,
            stopReason: response.StopReason,
            usage: response.Usage,
            errorMessage: response.ErrorMessage,
            provider: response.Provider,
            api: response.Api,
            responseModel: response.ResponseModel,
            responseId: response.ResponseId,
            rawStopReason: response.RawStopReason,
            endTurn: response.EndTurn,
            diagnostics: response.Diagnostics,
            deferred: response.Deferred);
        var normalized = ProviderTranscript.Normalize(
            new[] { assistant },
            _providerId,
            _apiId,
            request.Model,
            (id, _, _, _) =>
            {
                var identity = NormalizeToolIdentity(id, sameProtocol: true, sameModel: true);
                return identity.CallId + "|" + identity.ItemId;
            });
        return ProjectInput(
                request,
                normalized,
                new ReadOnlyDictionary<string, ToolDefinition>(
                    new Dictionary<string, ToolDefinition>(StringComparer.Ordinal)),
                includeSystemPrompt: false)
            .Select(item => JsonSerializer.Serialize(item))
            .ToArray();
    }

    private static void ThrowIfWebSocketProtocolError(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var eventType = type.GetString();
        var errorContainer = root;
        if (string.Equals(eventType, "response.failed", StringComparison.Ordinal)
            && root.TryGetProperty("response", out var failedResponse)
            && failedResponse.ValueKind == JsonValueKind.Object)
        {
            errorContainer = failedResponse;
        }
        else if (!string.Equals(eventType, "error", StringComparison.Ordinal))
        {
            return;
        }

        string? code = null;
        string? message = null;
        if (errorContainer.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            code = StringProperty(error, "code") ?? StringProperty(error, "type");
            message = StringProperty(error, "message");
        }

        code ??= StringProperty(errorContainer, "code") ?? "unknown";
        message ??= StringProperty(errorContainer, "message") ?? "The WebSocket service returned an error.";
        throw new OpenAIWebSocketProtocolException(code, message);
    }

    private static bool CanFallbackToServerSentEvents(Exception exception)
    {
        if (exception is OpenAIWebSocketProtocolException protocol)
        {
            return string.Equals(
                protocol.Code,
                "websocket_connection_limit_reached",
                StringComparison.OrdinalIgnoreCase);
        }

        return exception is not InvalidDataException
               && exception is not JsonException
               && exception is not ArgumentException
               && exception is not ModelProviderException;
    }

    private static string? StringProperty(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private bool IsWebSocketFallbackActive(string sessionId)
    {
        lock (_webSocketGate)
        {
            return _webSocketFallbackSessions.Contains(sessionId);
        }
    }

    private void RecordWebSocketRequest(string? sessionId, bool reused, bool delta)
    {
        if (sessionId is null)
        {
            return;
        }

        lock (_webSocketGate)
        {
            var statistics = GetOrCreateWebSocketStatistics(sessionId);
            statistics.Requests++;
            if (reused)
            {
                statistics.ConnectionsReused++;
            }
            else
            {
                statistics.ConnectionsCreated++;
            }

            if (delta)
            {
                statistics.DeltaRequests++;
            }
            else
            {
                statistics.FullContextRequests++;
            }
        }
    }

    private void RecordWebSocketFailure(string? sessionId, Exception exception)
    {
        if (sessionId is null)
        {
            return;
        }

        lock (_webSocketGate)
        {
            _webSocketFallbackSessions.Add(sessionId);
            var statistics = GetOrCreateWebSocketStatistics(sessionId);
            statistics.Failures++;
            statistics.LastError = BoundExceptionMessage(exception);
        }
    }

    private void RecordWebSocketSseFallback(string? sessionId)
    {
        if (sessionId is null)
        {
            return;
        }

        lock (_webSocketGate)
        {
            GetOrCreateWebSocketStatistics(sessionId).SseFallbacks++;
        }
    }

    private MutableWebSocketStatistics GetOrCreateWebSocketStatistics(string sessionId)
    {
        if (!_webSocketStatistics.TryGetValue(sessionId, out var value))
        {
            value = new MutableWebSocketStatistics();
            _webSocketStatistics[sessionId] = value;
        }

        return value;
    }

    private static ModelDiagnostic TransportDiagnostic(ModelTransport transport, string error) =>
        new(
            "provider_transport_fallback",
            "The WebSocket request failed before response output began; the request continued over server-sent events.",
            ModelDiagnosticSeverity.Warning,
            JsonSerializer.Serialize(new
            {
                configuredTransport = transport.ToString(),
                fallbackTransport = ModelTransport.ServerSentEvents.ToString(),
                phase = "before_response_output",
                error,
            }));

    private static string BoundExceptionMessage(Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
        return message.Length <= 4096 ? message : message.Substring(0, 4096);
    }

    private static async ValueTask DisposeIgnoringFailureAsync(IAsyncDisposable value)
    {
        try
        {
            await value.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task AwaitWithCancellationAsync(
        Task operation,
        CancellationToken cancellationToken)
    {
        if (operation.IsCompleted || !cancellationToken.CanBeCanceled)
        {
            await operation.ConfigureAwait(false);
            return;
        }

        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            () => canceled.TrySetResult(true));
        if (await Task.WhenAny(operation, canceled.Task).ConfigureAwait(false) != operation)
        {
            ObserveLateCompletion(operation);
            cancellationToken.ThrowIfCancellationRequested();
        }

        await operation.ConfigureAwait(false);
    }

    private static async Task<T> AwaitWithCancellationAsync<T>(
        Task<T> operation,
        CancellationToken cancellationToken,
        Action<T>? lateSuccess = null)
    {
        if (operation.IsCompleted || !cancellationToken.CanBeCanceled)
        {
            return await operation.ConfigureAwait(false);
        }

        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            () => canceled.TrySetResult(true));
        if (await Task.WhenAny(operation, canceled.Task).ConfigureAwait(false) != operation)
        {
            ObserveLateCompletion(operation, lateSuccess);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return await operation.ConfigureAwait(false);
    }

    private static void ObserveLateCompletion(Task operation)
    {
        _ = operation.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ObserveLateCompletion<T>(Task<T> operation, Action<T>? lateSuccess)
    {
        _ = operation.ContinueWith(
            completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion)
                {
                    try
                    {
                        lateSuccess?.Invoke(completed.Result);
                    }
                    catch
                    {
                    }
                }
                else if (completed.IsFaulted)
                {
                    _ = completed.Exception;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(OpenAIResponsesProvider));
        }
    }

    private static void ValidateOptions(OpenAIResponsesProviderOptions options)
    {
        if (!options.Endpoint.IsAbsoluteUri
            || options.Endpoint.UserInfo.Length > 0
            || (options.Endpoint.Scheme != Uri.UriSchemeHttp && options.Endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The endpoint must be an absolute HTTP or HTTPS URI without embedded credentials.", nameof(options));
        }

        if (options.Endpoint.Scheme == Uri.UriSchemeHttp && !options.Endpoint.IsLoopback && !options.AllowInsecureHttp)
        {
            throw new ArgumentException("Remote endpoints must use HTTPS unless insecure HTTP is explicitly enabled.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.ProviderId)
            || string.IsNullOrWhiteSpace(options.ApiId)
            || options.ProviderId.Length > 256
            || options.ApiId.Length > 256)
        {
            throw new ArgumentException("Provider and API identifiers must contain 1 to 256 characters.", nameof(options));
        }

        if (!Enum.IsDefined(typeof(OpenAISessionAffinityFormat), options.SessionAffinityFormat)
            || !Enum.IsDefined(typeof(OpenAIAuthenticationStyle), options.AuthenticationStyle)
            || !Enum.IsDefined(typeof(OpenAISystemPromptMode), options.SystemPromptMode)
            || options.TextVerbosity is { } verbosity && !Enum.IsDefined(typeof(OpenAITextVerbosity), verbosity)
            || options.ToolChoice is { } toolChoice && !Enum.IsDefined(typeof(OpenAIToolChoice), toolChoice)
            || options.MaxEventCharacters is < 1 or > 100_000_000
            || options.MaxErrorCharacters is < 1 or > 10_000_000
            || options.MaxRequestBytes is < 2 or > 100_000_000
            || options.MaxResponseCharacters is < 1 or > 100_000_000
            || options.MaxToolCallsPerResponse is < 1 or > 10_000
            || options.ResponseObserverTimeoutMilliseconds is < 1 or > 30_000
            || options.WebSocketIdleTimeoutMilliseconds is < 1 or > 86_400_000
            || options.WebSocketSessionIdleTimeoutMilliseconds is < 1 or > 86_400_000
            || options.WebSocketMaximumConnectionAgeMilliseconds is < 1 or > 86_400_000)
        {
            throw new ArgumentException("One or more provider bounds or compatibility settings are invalid.", nameof(options));
        }

        if (!options.SupportsWebSocketTransport && options.WebSocketConnectionFactory is not null)
        {
            throw new ArgumentException(
                "A WebSocket connection factory requires WebSocket transport support to be enabled.",
                nameof(options));
        }

        ValidateCredential(options.ApiKey, nameof(options));
        if (ProviderHeaderGuard.IsTransportControlledHeader(options.ApiKeyHeaderName))
        {
            throw new ArgumentException("The API key header is controlled by the transport.", nameof(options));
        }

        ValidateHeader(options.ApiKeyHeaderName, "placeholder", nameof(options));
        if (options.DefaultInstructions is null
            || options.DefaultInstructions.Length > options.MaxRequestBytes
            || (options.ReasoningSummary?.Length ?? 0) > 64
            || (options.ServiceTier?.Length ?? 0) > 64)
        {
            throw new ArgumentException("One or more Responses request defaults are invalid.", nameof(options));
        }

        if (options.GetCredentialAsync is not null && options.GetApiKeyAsync is not null)
        {
            throw new ArgumentException("Configure either a credential provider or an API-key provider, not both.", nameof(options));
        }
        ProviderHeaderGuard.ValidateMerge(options.Headers, nameof(options));
    }

    private void ApplyHeaders(
        HttpRequestMessage request,
        OpenAIRequestCredential credential,
        ModelRequest modelRequest)
    {
        var apiKey = credential.ApiKey;
        ValidateCredential(apiKey, nameof(OpenAIResponsesProviderOptions.GetApiKeyAsync));
        ProviderHeaderGuard.ValidateMerge(
            credential.Headers,
            nameof(OpenAIResponsesProviderOptions.GetCredentialAsync));
        var suppressedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ApplyHeaderLayer(request, _headers, suppressedHeaders, "provider");
        ApplyHeaderLayer(request, credential.Headers, suppressedHeaders, "credential");

        var credentialHeader = _authenticationStyle == OpenAIAuthenticationStyle.Bearer
            ? "Authorization"
            : _apiKeyHeaderName;
        var credentialValue = _authenticationStyle == OpenAIAuthenticationStyle.Bearer
            ? "Bearer " + apiKey
            : apiKey;
        if (_authenticationStyle != OpenAIAuthenticationStyle.None
            && !string.IsNullOrEmpty(apiKey)
            && !request.Headers.Contains(credentialHeader)
            && !request.Headers.TryAddWithoutValidation(credentialHeader, credentialValue))
        {
            throw new InvalidOperationException("The authorization header could not be applied.");
        }

        if (modelRequest.Parameters.CacheRetention == ModelCacheRetention.None
            || string.IsNullOrEmpty(modelRequest.SessionId))
        {
            return;
        }

        var sessionId = modelRequest.SessionId!;
        var affinityHeaders = _sessionAffinityFormat switch
        {
            OpenAISessionAffinityFormat.OpenRouter => new[] { ("x-session-id", sessionId) },
            OpenAISessionAffinityFormat.OpenAIWithoutSessionHeader => new[] { ("x-client-request-id", sessionId) },
            OpenAISessionAffinityFormat.Codex => new[] { ("session-id", sessionId), ("x-client-request-id", sessionId) },
            _ => new[] { ("session_id", sessionId), ("x-client-request-id", sessionId) },
        };
        foreach (var header in affinityHeaders)
        {
            if (!suppressedHeaders.Contains(header.Item1)
                && !request.Headers.Contains(header.Item1)
                && !request.Headers.TryAddWithoutValidation(header.Item1, header.Item2))
            {
                throw new InvalidOperationException($"Session header '{header.Item1}' is not valid for an HTTP request.");
            }
        }
    }

    private static void ApplyHeaderLayer(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string?> headers,
        ISet<string> suppressedHeaders,
        string layer)
    {
        foreach (var header in headers)
        {
            request.Headers.Remove(header.Key);
            if (header.Value is null)
            {
                suppressedHeaders.Add(header.Key);
                continue;
            }

            suppressedHeaders.Remove(header.Key);
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                throw new InvalidOperationException(
                    $"The {layer} header '{header.Key}' is not valid for an HTTP request.");
            }
        }
    }

    private byte[] SerializeRequest(ModelRequest request)
    {
        var normalizedMessages = ProviderTranscript.Normalize(
            request.Messages,
            _providerId,
            _apiId,
            request.Model,
            (id, _, _, _) =>
            {
                var identity = NormalizeToolIdentity(id, sameProtocol: false, sameModel: false);
                return identity.CallId + "|" + identity.ItemId;
            });
        var toolPlacement = SplitTools(request, normalizedMessages);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["input"] = ProjectInput(
                request,
                normalizedMessages,
                toolPlacement.Deferred,
                includeSystemPrompt: _systemPromptMode == OpenAISystemPromptMode.InputMessage),
            ["stream"] = true,
            ["store"] = false,
        };
        if (_systemPromptMode == OpenAISystemPromptMode.Instructions)
        {
            payload["instructions"] = request.SystemPrompt.Length == 0
                ? _defaultInstructions
                : request.SystemPrompt;
        }

        if (_serviceTier is not null)
        {
            payload["service_tier"] = _serviceTier;
        }

        if (_textVerbosity is { } verbosity)
        {
            payload["text"] = new Dictionary<string, object?>
            {
                ["verbosity"] = verbosity.ToString().ToLowerInvariant(),
            };
        }

        if (_toolChoice is { } toolChoice)
        {
            payload["tool_choice"] = toolChoice.ToString().ToLowerInvariant();
        }

        if (_parallelToolCalls is { } parallelToolCalls)
        {
            payload["parallel_tool_calls"] = parallelToolCalls;
        }
        if (request.Parameters.MaxOutputTokens is { } maximum)
        {
            payload["max_output_tokens"] = Math.Max(MinimumOutputTokens, maximum);
        }

        if (request.Parameters.Temperature is { } temperature)
        {
            payload["temperature"] = temperature;
        }

        if (request.Parameters.CacheRetention != ModelCacheRetention.None && request.SessionId is { } sessionId)
        {
            payload["prompt_cache_key"] = ClampUnicode(sessionId, 64);
        }

        if (request.Parameters.CacheRetention == ModelCacheRetention.Long && _supportsLongCacheRetention)
        {
            payload["prompt_cache_retention"] = "24h";
        }

        if (request.Parameters.CacheRetention == ModelCacheRetention.None && _supportsExplicitPromptCacheMode)
        {
            payload["prompt_cache_options"] = new Dictionary<string, object?> { ["mode"] = "explicit" };
        }

        if (toolPlacement.Immediate.Count > 0)
        {
            payload["tools"] = ProjectTools(toolPlacement.Immediate, deferLoading: false);
        }

        if (!string.IsNullOrWhiteSpace(request.Parameters.ReasoningLevel))
        {
            payload["reasoning"] = new Dictionary<string, object?>
            {
                ["effort"] = request.Parameters.ReasoningLevel,
                ["summary"] = _reasoningSummary ?? "auto",
            };
            payload["include"] = new[] { "reasoning.encrypted_content" };
        }
        else if (_alwaysIncludeEncryptedReasoning)
        {
            payload["include"] = new[] { "reasoning.encrypted_content" };
        }

        foreach (var extension in request.Parameters.Extensions)
        {
            if (payload.ContainsKey(extension.Key))
            {
                throw new InvalidOperationException($"Model extension '{extension.Key}' cannot override a core request field.");
            }

            payload[extension.Key] = ParseJsonOrString(extension.Value);
        }

        if (request.Parameters.SamplingParametersJson is { } sampling)
        {
            using var document = JsonDocument.Parse(sampling);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                payload[property.Name] = property.Value.Clone();
            }
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (body.Length > _maxRequestBytes)
        {
            throw new InvalidDataException("The Responses request exceeded the configured byte limit.");
        }

        return body;
    }

    private IReadOnlyList<object> ProjectInput(
        ModelRequest request,
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyDictionary<string, ToolDefinition> deferredTools,
        bool includeSystemPrompt)
    {
        var input = new List<object>();
        if (includeSystemPrompt && request.SystemPrompt.Length > 0)
        {
            input.Add(new Dictionary<string, object?>
            {
                ["role"] = _supportsDeveloperRole ? "developer" : "system",
                ["content"] = request.SystemPrompt,
            });
        }

        var grammarProperties = GrammarInputProperties(request.Tools);
        var loadedTools = new HashSet<string>(StringComparer.Ordinal);
        for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
        {
            var message = messages[messageIndex];
            if (message.Role is AgentRole.User or AgentRole.Custom)
            {
                var content = ProjectUserContent(message);
                if (content.Count > 0)
                {
                    input.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = content,
                    });
                }

                continue;
            }

            if (message.Role == AgentRole.Assistant)
            {
                var sameProtocol = string.Equals(message.Provider, _providerId, StringComparison.Ordinal)
                                   && string.Equals(message.Api, _apiId, StringComparison.Ordinal);
                var sameModel = sameProtocol && string.Equals(message.Model, request.Model, StringComparison.Ordinal);
                var textIndex = 0;
                foreach (var content in message.Content)
                {
                    switch (content)
                    {
                        case ReasoningContent reasoning when sameProtocol && !string.IsNullOrWhiteSpace(reasoning.Signature):
                            input.Add(ParseRequiredObject(reasoning.Signature!, "A reasoning signature must contain a JSON object."));
                            break;
                        case TextContent text:
                            var textIdentity = ParseTextIdentity(text.Signature);
                            var messageId = textIdentity.Id ?? $"msg_oga_{messageIndex}_{textIndex}";
                            textIndex++;
                            if (messageId.Length > 64)
                            {
                                messageId = "msg_" + ShortHash(messageId);
                            }

                            var outputMessage = new Dictionary<string, object?>
                            {
                                ["type"] = "message",
                                ["role"] = "assistant",
                                ["content"] = new object[]
                                {
                                    new Dictionary<string, object?>
                                    {
                                        ["type"] = "output_text",
                                        ["text"] = text.Text,
                                        ["annotations"] = Array.Empty<object>(),
                                    },
                                },
                                ["status"] = "completed",
                                ["id"] = messageId,
                            };
                            if (textIdentity.Phase is { } phase)
                            {
                                outputMessage["phase"] = phase;
                            }

                            input.Add(outputMessage);
                            break;
                        case ToolCallContent call:
                            var identity = NormalizeToolIdentity(call.Id, sameProtocol, sameModel);
                            var canReplayNamespace = sameModel || deferredTools.ContainsKey(call.Name);
                            if (grammarProperties.TryGetValue(call.Name, out var property))
                            {
                                using var arguments = JsonDocument.Parse(call.ArgumentsJson);
                                if (!arguments.RootElement.TryGetProperty(property, out var grammarInput)
                                    || grammarInput.ValueKind != JsonValueKind.String)
                                {
                                    throw new InvalidDataException(
                                        $"Grammar tool call '{call.Name}' requires string argument '{property}'.");
                                }

                                var customCall = new Dictionary<string, object?>
                                {
                                    ["type"] = "custom_tool_call",
                                    ["call_id"] = identity.CallId,
                                    ["name"] = call.Name,
                                    ["input"] = grammarInput.GetString(),
                                };
                                if (identity.ItemId is not null)
                                {
                                    customCall["id"] = identity.ItemId;
                                }

                                if (canReplayNamespace && call.Namespace is not null)
                                {
                                    customCall["namespace"] = call.Namespace;
                                }

                                input.Add(customCall);
                            }
                            else
                            {
                                var functionCall = new Dictionary<string, object?>
                                {
                                    ["type"] = "function_call",
                                    ["call_id"] = identity.CallId,
                                    ["name"] = call.Name,
                                    ["arguments"] = call.ArgumentsJson,
                                };
                                if (identity.ItemId is not null)
                                {
                                    functionCall["id"] = identity.ItemId;
                                }

                                if (canReplayNamespace && call.Namespace is not null)
                                {
                                    functionCall["namespace"] = call.Namespace;
                                }

                                input.Add(functionCall);
                            }

                            break;
                    }
                }

                continue;
            }

            if (message.Role == AgentRole.Tool)
            {
                var callId = message.ToolCallId!.Split('|')[0];
                input.Add(new Dictionary<string, object?>
                {
                    ["type"] = grammarProperties.ContainsKey(message.ToolName!)
                        ? "custom_tool_call_output"
                        : "function_call_output",
                    ["call_id"] = callId,
                    ["output"] = ProjectToolResultOutput(message.Content),
                });

                var additions = message.AddedToolNames
                    .Where(name => deferredTools.ContainsKey(name) && loadedTools.Add(name))
                    .Select(name => deferredTools[name])
                    .ToArray();
                if (additions.Length > 0 && _supportsAdditionalTools)
                {
                    input.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "additional_tools",
                        ["role"] = "developer",
                        ["tools"] = ProjectTools(additions, deferLoading: false),
                    });
                }
                else if (additions.Length > 0 && _supportsToolSearch)
                {
                    var names = additions.Select(tool => tool.Name).ToArray();
                    var searchCallId = "oga_tool_load_" + ShortHash(message.ToolCallId + ":" + string.Join(",", names));
                    input.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "tool_search_call",
                        ["call_id"] = searchCallId,
                        ["execution"] = "client",
                        ["status"] = "completed",
                        ["arguments"] = new Dictionary<string, object?>
                        {
                            ["query"] = string.Join(" ", names),
                            ["limit"] = names.Length,
                        },
                    });
                    input.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "tool_search_output",
                        ["call_id"] = searchCallId,
                        ["execution"] = "client",
                        ["status"] = "completed",
                        ["tools"] = ProjectTools(additions, deferLoading: true),
                    });
                }
            }
        }

        return input;
    }

    private static IReadOnlyList<object> ProjectUserContent(AgentMessage message)
    {
        var parts = new List<object>();
        if (message.Role == AgentRole.Custom)
        {
            parts.Add(new Dictionary<string, object?>
            {
                ["type"] = "input_text",
                ["text"] = "[" + message.CustomRole + "]",
            });
        }

        foreach (var content in message.Content)
        {
            switch (content)
            {
                case TextContent text:
                    parts.Add(new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = text.Text });
                    break;
                case JsonContent json:
                    parts.Add(new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = json.Json });
                    break;
                case BinaryContent binary when binary.MediaKind == AgentMediaKind.Image
                                               || binary.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                    parts.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "input_image",
                        ["detail"] = "auto",
                        ["image_url"] = $"data:{binary.MediaType};base64,{binary.Data}",
                    });
                    break;
                case ResourceContent resource when resource.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                    parts.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "input_image",
                        ["detail"] = "auto",
                        ["image_url"] = resource.Uri,
                    });
                    break;
                case ResourceContent resource:
                    parts.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "input_text",
                        ["text"] = $"[resource media_type={resource.MediaType}] {resource.Uri}",
                    });
                    break;
                case BinaryContent binary:
                    parts.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "input_text",
                        ["text"] = $"[binary media_type={binary.MediaType} data_omitted]",
                    });
                    break;
            }
        }

        return parts;
    }

    private static object ProjectToolResultOutput(IEnumerable<AgentContent> content)
    {
        var parts = new List<object>();
        var text = new List<string>();
        foreach (var item in content)
        {
            switch (item)
            {
                case TextContent value:
                    text.Add(value.Text);
                    break;
                case JsonContent value:
                    text.Add(value.Json);
                    break;
                case ResourceContent value when value.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                    parts.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "input_image",
                        ["detail"] = "auto",
                        ["image_url"] = value.Uri,
                    });
                    break;
                case BinaryContent value when value.MediaKind == AgentMediaKind.Image
                                               || value.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                    parts.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "input_image",
                        ["detail"] = "auto",
                        ["image_url"] = $"data:{value.MediaType};base64,{value.Data}",
                    });
                    break;
            }
        }

        if (parts.Count == 0)
        {
            return text.Count > 0 ? string.Join("\n", text) : "(no tool output)";
        }

        parts.Insert(0, new Dictionary<string, object?>
        {
            ["type"] = "input_text",
            ["text"] = text.Count > 0 ? string.Join("\n", text) : "(see attached image)",
        });
        return parts;
    }

    private object[] ProjectTools(IEnumerable<ToolDefinition> tools, bool deferLoading)
    {
        return tools.Select(tool => ProjectTool(tool, deferLoading)).ToArray();
    }

    private object ProjectTool(ToolDefinition tool, bool deferLoading)
    {
        if (tool.ConstrainedSampling?.Kind == ToolConstrainedSamplingKind.Grammar && _supportsGrammarTools)
        {
            _ = InferGrammarInputProperty(tool);
            var syntax = !string.IsNullOrWhiteSpace(tool.ConstrainedSampling.OpenAiLark) ? "lark" : "regex";
            var definition = syntax == "lark"
                ? tool.ConstrainedSampling.OpenAiLark
                : tool.ConstrainedSampling.OpenAiRegex;
            var custom = new Dictionary<string, object?>
            {
                ["type"] = "custom",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["format"] = new Dictionary<string, object?>
                {
                    ["type"] = "grammar",
                    ["syntax"] = syntax,
                    ["definition"] = definition,
                },
            };
            if (deferLoading)
            {
                custom["defer_loading"] = true;
            }

            return custom;
        }

        if (tool.ConstrainedSampling?.Kind == ToolConstrainedSamplingKind.JsonSchema
            && tool.ConstrainedSampling.Strictness == ToolSchemaStrictness.Require
            && !_supportsStrictTools)
        {
            throw new InvalidOperationException(
                $"Tool '{tool.Name}' requires strict JSON-schema sampling, but the endpoint does not support it.");
        }

        var function = new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["parameters"] = ParseRequiredObject(tool.InputSchemaJson, "A tool schema must be a JSON object."),
        };
        if (_supportsStrictTools)
        {
            function["strict"] = tool.ConstrainedSampling?.Kind == ToolConstrainedSamplingKind.JsonSchema;
        }

        if (deferLoading)
        {
            function["defer_loading"] = true;
        }

        return function;
    }

    private static JsonElement ParseRequiredObject(string json, string message)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(message);
        }

        return document.RootElement.Clone();
    }

    private static (string? Id, string? Phase) ParseTextIdentity(string? signature)
    {
        if (string.IsNullOrEmpty(signature))
        {
            return (null, null);
        }

        if (signature.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(signature);
                var root = document.RootElement;
                if (root.TryGetProperty("v", out var version)
                    && version.TryGetInt32(out var parsedVersion)
                    && parsedVersion == 1
                    && root.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String)
                {
                    var phase = root.TryGetProperty("phase", out var phaseElement)
                                && phaseElement.ValueKind == JsonValueKind.String
                        ? phaseElement.GetString()
                        : null;
                    return (id.GetString(), phase is "commentary" or "final_answer" ? phase : null);
                }
            }
            catch (JsonException)
            {
            }
        }

        return (signature, null);
    }

    private static (string CallId, string? ItemId) NormalizeToolIdentity(
        string id,
        bool sameProtocol,
        bool sameModel)
    {
        var split = id.Split('|');
        var callId = NormalizeId(split[0], "call");
        var rawItemId = split.Length > 1 ? split[1] : null;
        var itemId = rawItemId is null ? null : NormalizeId(rawItemId, "fc");
        if (!sameProtocol && rawItemId?.StartsWith("fc_", StringComparison.Ordinal) != true)
        {
            itemId = "fc_" + ShortHash(id);
        }
        else if (!sameModel && itemId?.StartsWith("fc_", StringComparison.Ordinal) == true)
        {
            itemId = null;
        }
        else if (itemId is not null && !itemId.StartsWith("fc_", StringComparison.Ordinal))
        {
            itemId = "fc_" + itemId;
        }

        return (callId, itemId);
    }

    private static string NormalizeId(string value, string prefix)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return prefix + "_" + ShortHash(value ?? string.Empty);
        }

        var valid = value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-');
        var normalized = valid ? value : prefix + "_" + ShortHash(value);
        return normalized.Length <= 64 ? normalized : prefix + "_" + ShortHash(normalized);
    }

    private ToolPlacement SplitTools(ModelRequest request, IReadOnlyList<AgentMessage> messages)
    {
        var supportsDeferred = _supportsAdditionalTools || _supportsToolSearch;
        if (!supportsDeferred)
        {
            return new ToolPlacement(request.Tools, new Dictionary<string, ToolDefinition>(StringComparer.Ordinal));
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var deferredNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role == AgentRole.Assistant)
            {
                foreach (var call in message.Content.OfType<ToolCallContent>())
                {
                    used.Add(call.Name);
                }
            }
            else if (message.Role == AgentRole.Tool)
            {
                foreach (var name in message.AddedToolNames)
                {
                    if (!used.Contains(name))
                    {
                        deferredNames.Add(name);
                    }
                }
            }
        }

        var unique = request.Tools.GroupBy(tool => tool.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var deferred = unique.Where(pair => deferredNames.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var immediate = unique.Where(pair => !deferredNames.Contains(pair.Key)).Select(pair => pair.Value).ToArray();
        return new ToolPlacement(immediate, deferred);
    }

    private sealed class ToolPlacement
    {
        public ToolPlacement(
            IReadOnlyList<ToolDefinition> immediate,
            IReadOnlyDictionary<string, ToolDefinition> deferred)
        {
            Immediate = immediate;
            Deferred = deferred;
        }

        public IReadOnlyList<ToolDefinition> Immediate { get; }

        public IReadOnlyDictionary<string, ToolDefinition> Deferred { get; }
    }

    private static object? ParseJsonOrString(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static string ClampUnicode(string value, int maximumCharacters)
    {
        if (value.Length <= maximumCharacters)
        {
            return value;
        }

        return value.Substring(0, maximumCharacters);
    }

    private static void ValidateCredential(string? value, string parameterName)
    {
        if ((value?.Length ?? 0) > 65_536
            || (value is { Length: > 0 } && string.IsNullOrWhiteSpace(value))
            || value?.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
        {
            throw new ArgumentException("A credential is empty, too large, or contains invalid control characters.", parameterName);
        }
    }

    private static void ValidateHeader(string name, string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Length > 256
            || value is null
            || value.Length > 65_536
            || name.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0
            || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
        {
            throw new ArgumentException("HTTP headers are empty, too large, or contain invalid control characters.", parameterName);
        }
    }

    private static async Task<string> ReadBoundedAsync(
        HttpContent content,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var registration = cancellationToken.Register(stream.Dispose);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
        var buffer = new char[Math.Min(4096, maximumCharacters)];
        var builder = new StringBuilder();
        while (builder.Length < maximumCharacters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await reader.ReadAsync(buffer, 0, Math.Min(buffer.Length, maximumCharacters - builder.Length))
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }

    private static async IAsyncEnumerable<string> ReadBoundedLinesAsync(
        StreamReader reader,
        int maximumCharacters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<char>.Shared.Rent(Math.Min(4096, maximumCharacters + 1));
        var line = new StringBuilder();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read;
                try
                {
                    read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
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
                        if (line.Length > maximumCharacters)
                        {
                            throw new InvalidDataException("A Responses stream event exceeded the configured size limit.");
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
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

    private static string ShortHash(string value)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
        var builder = new StringBuilder(16);
        for (var index = 0; index < 8; index++)
        {
            builder.Append(bytes[index].ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private IReadOnlyDictionary<string, string> GrammarInputProperties(IEnumerable<ToolDefinition> tools)
    {
        return tools.Where(tool => tool.ConstrainedSampling?.Kind == ToolConstrainedSamplingKind.Grammar)
            .ToDictionary(tool => tool.Name, InferGrammarInputProperty, StringComparer.Ordinal);
    }

    private static string InferGrammarInputProperty(ToolDefinition tool)
    {
        using var document = JsonDocument.Parse(tool.InputSchemaJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var type)
            || type.GetString() != "object"
            || !root.TryGetProperty("required", out var required)
            || required.ValueKind != JsonValueKind.Array
            || required.GetArrayLength() != 1
            || required[0].ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"Grammar tool '{tool.Name}' requires an object schema with exactly one required string property.");
        }

        var property = required[0].GetString()!;
        if (!root.TryGetProperty("properties", out var properties)
            || !properties.TryGetProperty(property, out var schema)
            || !schema.TryGetProperty("type", out var propertyType)
            || propertyType.GetString() != "string")
        {
            throw new InvalidOperationException(
                $"Grammar tool '{tool.Name}' requires its sole required property to be a string.");
        }

        return property;
    }

    private sealed class OpenAIWebSocketProtocolException : IOException
    {
        public OpenAIWebSocketProtocolException(string code, string message)
            : base($"WebSocket protocol error {code}: {message}")
        {
            Code = code;
        }

        public string Code { get; }
    }

    private sealed class WebSocketLease
    {
        private readonly Action<bool> _release;
        private int _released;

        public WebSocketLease(
            IOpenAIWebSocketConnection connection,
            CachedWebSocketConnection? entry,
            bool reused,
            Action<bool> release)
        {
            Connection = connection;
            Entry = entry;
            Reused = reused;
            _release = release;
        }

        public IOpenAIWebSocketConnection Connection { get; }

        public CachedWebSocketConnection? Entry { get; }

        public bool Reused { get; }

        public void Release(bool keep)
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _release(keep);
            }
        }
    }

    private sealed class CachedWebSocketConnection : IDisposable
    {
        private int _disposed;

        public CachedWebSocketConnection(IOpenAIWebSocketConnection connection)
        {
            Connection = connection;
            CreatedAt = DateTimeOffset.UtcNow;
            Busy = true;
        }

        public IOpenAIWebSocketConnection Connection { get; }

        public DateTimeOffset CreatedAt { get; }

        public bool Busy { get; set; }

        public Timer? IdleTimer { get; set; }

        public WebSocketContinuation? Continuation { get; set; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            IdleTimer?.Dispose();
            Connection.Dispose();
        }
    }

    private sealed class WebSocketContinuation
    {
        public WebSocketContinuation(string fingerprint, string responseId, IReadOnlyList<string> baselineItems)
        {
            Fingerprint = fingerprint;
            ResponseId = responseId;
            BaselineItems = baselineItems;
        }

        public string Fingerprint { get; }

        public string ResponseId { get; }

        public IReadOnlyList<string> BaselineItems { get; }
    }

    private sealed class RequestBodyDelta
    {
        public RequestBodyDelta(string responseId, IReadOnlyList<string> items)
        {
            ResponseId = responseId;
            Items = items;
        }

        public string ResponseId { get; }

        public IReadOnlyList<string> Items { get; }
    }

    private sealed class RequestBodySnapshot
    {
        private readonly byte[] _body;

        private RequestBodySnapshot(byte[] body, string fingerprint, IReadOnlyList<string> inputItems)
        {
            _body = body;
            Fingerprint = fingerprint;
            InputItems = inputItems;
        }

        public string Fingerprint { get; }

        public IReadOnlyList<string> InputItems { get; }

        public static RequestBodySnapshot Create(byte[] body)
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 128 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("input", out var input)
                || input.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The Responses request did not contain an input array.");
            }

            var items = input.EnumerateArray().Select(item => item.GetRawText()).ToArray();
            using var canonical = new MemoryStream();
            using (var writer = new Utf8JsonWriter(canonical))
            {
                writer.WriteStartObject();
                foreach (var property in root.EnumerateObject())
                {
                    if (property.NameEquals("input") || property.NameEquals("previous_response_id"))
                    {
                        continue;
                    }

                    property.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            using var sha = SHA256.Create();
            var fingerprint = Convert.ToBase64String(sha.ComputeHash(canonical.ToArray()));
            return new RequestBodySnapshot(body.ToArray(), fingerprint, items);
        }

        public RequestBodyDelta? TryCreateDelta(WebSocketContinuation continuation)
        {
            if (!string.Equals(Fingerprint, continuation.Fingerprint, StringComparison.Ordinal)
                || InputItems.Count < continuation.BaselineItems.Count)
            {
                return null;
            }

            for (var index = 0; index < continuation.BaselineItems.Count; index++)
            {
                if (!string.Equals(
                    InputItems[index],
                    continuation.BaselineItems[index],
                    StringComparison.Ordinal))
                {
                    return null;
                }
            }

            return new RequestBodyDelta(
                continuation.ResponseId,
                InputItems.Skip(continuation.BaselineItems.Count).ToArray());
        }

        public string CreateWebSocketRequest(RequestBodyDelta? delta)
        {
            using var document = JsonDocument.Parse(_body, new JsonDocumentOptions { MaxDepth = 128 });
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "response.create");
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("previous_response_id"))
                    {
                        continue;
                    }

                    if (delta is not null && property.NameEquals("input"))
                    {
                        writer.WritePropertyName("input");
                        writer.WriteStartArray();
                        foreach (var item in delta.Items)
                        {
                            using var itemDocument = JsonDocument.Parse(item, new JsonDocumentOptions { MaxDepth = 128 });
                            itemDocument.RootElement.WriteTo(writer);
                        }

                        writer.WriteEndArray();
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }

                if (delta is not null)
                {
                    writer.WriteString("previous_response_id", delta.ResponseId);
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(output.ToArray());
        }
    }

    private sealed class MutableWebSocketStatistics
    {
        public long Requests { get; set; }

        public long ConnectionsCreated { get; set; }

        public long ConnectionsReused { get; set; }

        public long FullContextRequests { get; set; }

        public long DeltaRequests { get; set; }

        public long Failures { get; set; }

        public long SseFallbacks { get; set; }

        public string? LastError { get; set; }

        public OpenAIWebSocketStatistics Snapshot(bool fallbackActive) =>
            new(
                Requests,
                ConnectionsCreated,
                ConnectionsReused,
                FullContextRequests,
                DeltaRequests,
                Failures,
                SseFallbacks,
                fallbackActive,
                LastError);
    }
}
