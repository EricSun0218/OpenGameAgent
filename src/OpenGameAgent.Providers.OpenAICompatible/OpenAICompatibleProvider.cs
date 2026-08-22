using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;
using OpenGameAgent.ProviderTransport;

namespace OpenGameAgent.Providers.OpenAICompatible;

public delegate ValueTask<string?> ApiKeyProvider(CancellationToken cancellationToken);

public delegate string? OpenAICompatibleResourcePartProjector(ResourceContent resource);

public enum OpenAICompatibleMaxTokensField
{
    MaxTokens,
    MaxCompletionTokens,
}

public enum OpenAICompatibleThinkingFormat
{
    OpenAI,
    OpenRouter,
    DeepSeek,
    Together,
    Baseten,
    Zai,
    Qwen,
    ChatTemplate,
    QwenChatTemplate,
    StringThinking,
    AntLing,
}

public enum OpenAICompatibleSessionAffinityFormat
{
    OpenAI,
    OpenAIWithoutSessionHeader,
    OpenRouter,
}

public enum OpenAICompatibleCacheControlFormat
{
    None,
    Anthropic,
}

public enum OpenAICompatibleDeferredToolsMode
{
    None,
    Kimi,
}

/// <summary>
/// Explicit protocol switches for endpoints that implement different subsets of the
/// chat-completions wire format. Values are snapshotted when the provider is constructed.
/// </summary>
public sealed class OpenAICompatibleProtocolOptions
{
    public bool SupportsStore { get; set; }

    public bool SupportsDeveloperRole { get; set; }

    public bool SupportsReasoningEffort { get; set; } = true;

    public bool SupportsUsageInStreaming { get; set; } = true;

    public bool SupportsFinishReason { get; set; } = true;

    public OpenAICompatibleMaxTokensField MaxTokensField { get; set; } = OpenAICompatibleMaxTokensField.MaxTokens;

    public bool RequiresToolResultName { get; set; }

    public bool RequiresAssistantAfterToolResult { get; set; }

    public bool RequiresThinkingAsText { get; set; }

    public bool RequiresReasoningContentOnAssistantMessages { get; set; }

    public OpenAICompatibleThinkingFormat ThinkingFormat { get; set; } = OpenAICompatibleThinkingFormat.OpenAI;

    public bool ZaiToolStream { get; set; }

    public bool SupportsThinkingTokenBudget { get; set; }

    public bool SupportsStrictMode { get; set; } = true;

    public bool SupportsGrammarTools { get; set; }

    public OpenAICompatibleCacheControlFormat CacheControlFormat { get; set; }

    public bool SendSessionAffinityHeaders { get; set; }

    public OpenAICompatibleSessionAffinityFormat SessionAffinityFormat { get; set; } =
        OpenAICompatibleSessionAffinityFormat.OpenAI;

    public OpenAICompatibleDeferredToolsMode DeferredToolsMode { get; set; }

    public bool SupportsLongCacheRetention { get; set; } = true;

    public string? ChatTemplateArgumentsJson { get; set; }

    public string? ChatTemplateKeywordArgumentsJson { get; set; }

    internal OpenAICompatibleProtocolOptions Copy() => new()
    {
        SupportsStore = SupportsStore,
        SupportsDeveloperRole = SupportsDeveloperRole,
        SupportsReasoningEffort = SupportsReasoningEffort,
        SupportsUsageInStreaming = SupportsUsageInStreaming,
        SupportsFinishReason = SupportsFinishReason,
        MaxTokensField = MaxTokensField,
        RequiresToolResultName = RequiresToolResultName,
        RequiresAssistantAfterToolResult = RequiresAssistantAfterToolResult,
        RequiresThinkingAsText = RequiresThinkingAsText,
        RequiresReasoningContentOnAssistantMessages = RequiresReasoningContentOnAssistantMessages,
        ThinkingFormat = ThinkingFormat,
        ZaiToolStream = ZaiToolStream,
        SupportsThinkingTokenBudget = SupportsThinkingTokenBudget,
        SupportsStrictMode = SupportsStrictMode,
        SupportsGrammarTools = SupportsGrammarTools,
        CacheControlFormat = CacheControlFormat,
        SendSessionAffinityHeaders = SendSessionAffinityHeaders,
        SessionAffinityFormat = SessionAffinityFormat,
        DeferredToolsMode = DeferredToolsMode,
        SupportsLongCacheRetention = SupportsLongCacheRetention,
        ChatTemplateArgumentsJson = ChatTemplateArgumentsJson,
        ChatTemplateKeywordArgumentsJson = ChatTemplateKeywordArgumentsJson,
    };
}

public sealed class OpenAICompatibleProviderOptions
{
    public OpenAICompatibleProviderOptions(HttpClient httpClient, Uri endpoint)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        if (!endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("The model endpoint must be an absolute URI.", nameof(endpoint));
        }


        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The model endpoint must use HTTP or HTTPS.", nameof(endpoint));
        }
    }

    public HttpClient HttpClient { get; }

    public Uri Endpoint { get; set; }

    public string? ApiKey { get; set; }

    public ApiKeyProvider? GetApiKeyAsync { get; set; }

    public string ApiKeyHeader { get; set; } = "Authorization";

    public string ApiKeyScheme { get; set; } = "Bearer";

    public bool AllowInsecureHttp { get; set; }

    public IDictionary<string, string?> Headers { get; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    public ProviderResponseObserver? ResponseObserver { get; set; }

    public int ResponseObserverTimeoutMilliseconds { get; set; } =
        ProviderResponseObserverRunner.DefaultTimeoutMilliseconds;

    public int MaxEventCharacters { get; set; } = 4_000_000;

    public int MaxErrorCharacters { get; set; } = 64_000;

    public int MaxRequestBytes { get; set; } = 16_000_000;

    public int MaxResponseCharacters { get; set; } = 16_000_000;

    public int MaxToolCallsPerResponse { get; set; } = 256;

    public bool IncludeUsage { get; set; } = true;

    public bool AllowDoneWithoutFinishReason { get; set; }

    public IList<string> ReasoningDeltaFields { get; } = new List<string>
    {
        "reasoning_content",
        "reasoning",
    };

    /// <summary>
    /// Called after an HTTP 401 or 403 response. Use this to invalidate a cached short-lived
    /// gateway credential. The failed streamed request is not retried automatically.
    /// </summary>
    public Action<HttpStatusCode>? OnAuthenticationFailure { get; set; }

    /// <summary>
    /// Converts a resource into a provider-specific multimodal content-part JSON object.
    /// Return null to use the built-in image projection or plain resource text fallback.
    /// </summary>
    public OpenAICompatibleResourcePartProjector? ProjectResourcePart { get; set; }

    public string ProviderId { get; set; } = "openai-compatible";

    public string ApiId { get; set; } = "openai-completions";

    public OpenAICompatibleProtocolOptions Protocol { get; } = new();
}

public sealed class OpenAICompatibleProvider : IModelProvider, IModelProviderCapabilities
{
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly string? _apiKey;
    private readonly ApiKeyProvider? _getApiKey;
    private readonly string _apiKeyHeader;
    private readonly string _apiKeyScheme;
    private readonly IReadOnlyDictionary<string, string?> _headers;
    private readonly ProviderResponseObserver? _responseObserver;
    private readonly int _responseObserverTimeoutMilliseconds;
    private readonly int _maxEventCharacters;
    private readonly int _maxErrorCharacters;
    private readonly int _maxRequestBytes;
    private readonly int _maxResponseCharacters;
    private readonly int _maxToolCallsPerResponse;
    private readonly bool _includeUsage;
    private readonly bool _allowDoneWithoutFinishReason;
    private readonly IReadOnlyList<string> _reasoningDeltaFields;
    private readonly Action<HttpStatusCode>? _onAuthenticationFailure;
    private readonly OpenAICompatibleResourcePartProjector? _projectResourcePart;
    private readonly string _providerId;
    private readonly string _apiId;
    private readonly OpenAICompatibleProtocolOptions _protocol;
    private readonly IReadOnlyCollection<string> _supportedApis;

    public IReadOnlyCollection<string> SupportedApis => _supportedApis;

    public bool SupportsNativeDeferredTools => _protocol.DeferredToolsMode != OpenAICompatibleDeferredToolsMode.None;

    public bool SupportsDeferredResponses => false;

    public OpenAICompatibleProvider(OpenAICompatibleProviderOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (options.MaxEventCharacters < 1 || options.MaxEventCharacters > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum event size is invalid.");
        }

        if (options.MaxErrorCharacters < 1 || options.MaxErrorCharacters > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum error size is invalid.");
        }

        if (options.MaxRequestBytes < 2 || options.MaxRequestBytes > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum request size is invalid.");
        }

        if (options.MaxResponseCharacters < 1 || options.MaxResponseCharacters > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum response size is invalid.");
        }

        if (options.MaxToolCallsPerResponse < 1 || options.MaxToolCallsPerResponse > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum tool-call count is invalid.");
        }

        if (options.ResponseObserverTimeoutMilliseconds is < 1 or > 30_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The response observer timeout is invalid.");
        }

        var reasoningFields = options.ReasoningDeltaFields
            .Select(field => string.IsNullOrWhiteSpace(field) || field.Length > 128
                ? throw new ArgumentException("Reasoning delta field names must contain 1 to 128 characters.", nameof(options))
                : field)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (reasoningFields.Length == 0)
        {
            throw new ArgumentException("At least one reasoning delta field is required.", nameof(options));
        }

        if (reasoningFields.Any(field => field is "content" or "tool_calls" or "role"))
        {
            throw new ArgumentException("Reasoning delta fields cannot reuse core stream fields.", nameof(options));
        }


        if (options.Endpoint is null
            || !options.Endpoint.IsAbsoluteUri
            || options.Endpoint.UserInfo.Length > 0
            || (!string.Equals(options.Endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(options.Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The model endpoint must be an absolute HTTP or HTTPS URI.", nameof(options));
        }

        if (string.Equals(options.Endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !options.Endpoint.IsLoopback
            && !options.AllowInsecureHttp)
        {
            throw new ArgumentException(
                "Remote model endpoints must use HTTPS unless insecure HTTP is explicitly enabled.",
                nameof(options));
        }

        if (ProviderHeaderGuard.IsTransportControlledHeader(options.ApiKeyHeader))
        {
            throw new ArgumentException("The API key header is controlled by the transport.", nameof(options));
        }

        ValidateHeader(options.ApiKeyHeader, string.Empty, nameof(options));
        ValidateCredential(options.ApiKey, nameof(options));
        ValidateCredential(options.ApiKeyScheme, nameof(options));
        ProviderHeaderGuard.ValidateMerge(options.Headers, nameof(options));

        if ((!string.IsNullOrEmpty(options.ApiKey) || options.GetApiKeyAsync is not null)
            && options.Headers.TryGetValue(options.ApiKeyHeader, out var configuredApiKeyHeader)
            && configuredApiKeyHeader is not null)
        {
            throw new ArgumentException("Custom headers cannot also define the configured API key header.", nameof(options));
        }

        _httpClient = options.HttpClient;
        _endpoint = options.Endpoint;
        _apiKey = options.ApiKey;
        _getApiKey = options.GetApiKeyAsync;
        _apiKeyHeader = string.IsNullOrWhiteSpace(options.ApiKeyHeader)
            ? throw new ArgumentException("An API key header is required.", nameof(options))
            : options.ApiKeyHeader;
        _apiKeyScheme = options.ApiKeyScheme ?? string.Empty;
        _headers = new Dictionary<string, string?>(options.Headers, StringComparer.OrdinalIgnoreCase);
        _responseObserver = options.ResponseObserver;
        _responseObserverTimeoutMilliseconds = options.ResponseObserverTimeoutMilliseconds;
        _maxEventCharacters = options.MaxEventCharacters;
        _maxErrorCharacters = options.MaxErrorCharacters;
        _maxRequestBytes = options.MaxRequestBytes;
        _maxResponseCharacters = options.MaxResponseCharacters;
        _maxToolCallsPerResponse = options.MaxToolCallsPerResponse;
        _includeUsage = options.IncludeUsage;
        _allowDoneWithoutFinishReason = options.AllowDoneWithoutFinishReason;
        _reasoningDeltaFields = Array.AsReadOnly(reasoningFields);
        _onAuthenticationFailure = options.OnAuthenticationFailure;
        _projectResourcePart = options.ProjectResourcePart;
        _providerId = RequireIdentifier(options.ProviderId, nameof(options));
        _apiId = RequireIdentifier(options.ApiId, nameof(options));
        _supportedApis = Array.AsReadOnly(new[] { _apiId });
        _protocol = options.Protocol.Copy();
        ValidateProtocol(_protocol, nameof(options));
    }

    private static string RequireIdentifier(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 256
            ? throw new ArgumentException("Provider and API identifiers must contain 1 to 256 characters.", parameterName)
            : value;

    private static void ValidateProtocol(OpenAICompatibleProtocolOptions protocol, string parameterName)
    {
        if (!Enum.IsDefined(typeof(OpenAICompatibleMaxTokensField), protocol.MaxTokensField)
            || !Enum.IsDefined(typeof(OpenAICompatibleThinkingFormat), protocol.ThinkingFormat)
            || !Enum.IsDefined(typeof(OpenAICompatibleCacheControlFormat), protocol.CacheControlFormat)
            || !Enum.IsDefined(typeof(OpenAICompatibleSessionAffinityFormat), protocol.SessionAffinityFormat)
            || !Enum.IsDefined(typeof(OpenAICompatibleDeferredToolsMode), protocol.DeferredToolsMode))
        {
            throw new ArgumentException("One or more protocol compatibility values are invalid.", parameterName);
        }

        ValidateJsonObject(protocol.ChatTemplateArgumentsJson, parameterName);
        ValidateJsonObject(protocol.ChatTemplateKeywordArgumentsJson, parameterName);
    }

    private static void ValidateJsonObject(string? json, string parameterName)
    {
        if (json is null)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            EnsureUnambiguous(document.RootElement, "A protocol JSON object contains duplicate property names.");
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Protocol JSON settings must be JSON objects.", parameterName);
            }
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Protocol JSON settings must contain valid JSON objects.", parameterName, exception);
        }
    }

    private static void ValidateHeader(string name, string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Length > 256
            || value is null
            || value.Length > 65_536
            || value.Contains('\r')
            || value.Contains('\n')
            || value.Contains('\0'))
        {
            throw new ArgumentException("HTTP header names must be non-empty, and names and values cannot contain line breaks.", parameterName);
        }

        try
        {
            using var request = new HttpRequestMessage();
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                throw new ArgumentException("The HTTP header name is not valid for request headers.", parameterName);
            }
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The HTTP header name is invalid.", parameterName, exception);
        }
    }

    private static void ValidateCredential(string? value, string parameterName)
    {
        if (value is { Length: > 0 } && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A configured credential cannot contain only whitespace.", parameterName);
        }

        if ((value?.Length ?? 0) > 65_536)
        {
            throw new ArgumentException("Credentials cannot exceed 65536 characters.", parameterName);
        }

        if ((value?.Contains('\r') ?? false)
            || (value?.Contains('\n') ?? false)
            || (value?.Contains('\0') ?? false))
        {
            throw new ArgumentException("Credentials contain invalid control characters.", parameterName);
        }
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.Parameters.Transport is ModelTransport.WebSocket or ModelTransport.CachedWebSocket)
        {
            throw new NotSupportedException("This provider uses server-sent events and cannot satisfy a WebSocket-only request.");
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        var apiKey = _getApiKey is null
            ? _apiKey
            : await ProviderCallbackRunner.RunAsync(
                    token => _getApiKey(token),
                    cancellationToken)
                .ConfigureAwait(false);
        ValidateCredential(apiKey, nameof(OpenAICompatibleProviderOptions.GetApiKeyAsync));
        ApplyHeaders(httpRequest, apiKey, request.SessionId);
        var requestBody = SerializeRequest(request, out var requestFields);
        httpRequest.Content = new ByteArrayContent(requestBody);
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
            Exception? authenticationFailureException = null;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                try
                {
                    _onAuthenticationFailure?.Invoke(response.StatusCode);
                }
                catch (Exception exception)
                {
                    authenticationFailureException = exception;
                }
            }

            var error = await ReadBoundedAsync(response.Content, _maxErrorCharacters, cancellationToken).ConfigureAwait(false);
            var retry = ProviderHttpRetryMetadata.FromResponse(response, errorText: error);
            throw new ModelProviderException(
                $"The model endpoint returned HTTP {(int)response.StatusCode}.",
                new[]
                {
                    new ModelDiagnostic(
                        "openai_compatible_http_error",
                        "The OpenAI-compatible endpoint returned an unsuccessful HTTP response.",
                        ModelDiagnosticSeverity.Error,
                        JsonSerializer.Serialize(new
                        {
                            version = 1,
                            statusCode = (int)response.StatusCode,
                            category = ClassifyHttpFailure(response.StatusCode),
                            requestFields,
                            providerRequestId = ReadProviderRequestId(response),
                        })),
                },
                retry.IsTransient,
                retry.RetryAfter,
                (int)response.StatusCode,
                authenticationFailureException);
        }

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var cancellationRegistration = cancellationToken.Register(stream.Dispose);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
        var state = new StreamState(
            request.Model,
            _providerId,
            _apiId,
            _maxResponseCharacters,
            _maxToolCallsPerResponse,
            _reasoningDeltaFields);
        var sawDone = false;
        yield return ModelStreamEvent.Update(ModelStreamEventKind.Started, state.Partial());

        await foreach (var line in ReadBoundedLinesAsync(reader, _maxEventCharacters, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line.Substring(5).TrimStart();
            if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
            {
                sawDone = true;
                break;
            }

            if (data.Length == 0)
            {
                continue;
            }

            foreach (var update in state.Apply(data))
            {
                yield return update;
            }
        }

        if (!state.HasFinishReason
            && _protocol.SupportsFinishReason
            && !(sawDone && _allowDoneWithoutFinishReason))
        {
            throw new InvalidDataException("The model stream ended before receiving a finish reason.");
        }

        if (!state.HasFinishReason && !_protocol.SupportsFinishReason)
        {
            state.InferStopReason();
        }

        yield return ModelStreamEvent.Terminal(state.Complete());
    }

    private void ApplyHeaders(HttpRequestMessage request, string? apiKey, string? sessionId)
    {
        var suppressedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in _headers)
        {
            request.Headers.Remove(header.Key);
            if (header.Value is null)
            {
                suppressedHeaders.Add(header.Key);
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                throw new InvalidOperationException($"Header '{header.Key}' is not valid for an HTTP request.");
            }
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            ApplySessionHeaders(request, sessionId, suppressedHeaders);
            return;
        }

        var value = string.IsNullOrWhiteSpace(_apiKeyScheme) ? apiKey : _apiKeyScheme + " " + apiKey;
        if (!request.Headers.TryAddWithoutValidation(_apiKeyHeader, value))
        {
            throw new InvalidOperationException($"API key header '{_apiKeyHeader}' is not valid for an HTTP request.");
        }

        ApplySessionHeaders(request, sessionId, suppressedHeaders);
    }

    private void ApplySessionHeaders(
        HttpRequestMessage request,
        string? sessionId,
        ISet<string> suppressedHeaders)
    {
        if (!_protocol.SendSessionAffinityHeaders || string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        var headers = _protocol.SessionAffinityFormat switch
        {
            OpenAICompatibleSessionAffinityFormat.OpenRouter => new[] { ("x-session-id", sessionId) },
            OpenAICompatibleSessionAffinityFormat.OpenAIWithoutSessionHeader => new[]
            {
                ("x-client-request-id", sessionId),
                ("x-session-affinity", sessionId),
            },
            _ => new[]
            {
                ("session_id", sessionId),
                ("x-client-request-id", sessionId),
                ("x-session-affinity", sessionId),
            },
        };
        foreach (var header in headers)
        {
            if (!suppressedHeaders.Contains(header.Item1)
                && !request.Headers.Contains(header.Item1)
                && !request.Headers.TryAddWithoutValidation(header.Item1, header.Item2))
            {
                throw new InvalidOperationException($"Session header '{header.Item1}' is not valid for an HTTP request.");
            }
        }
    }

    private byte[] SerializeRequest(ModelRequest request, out IReadOnlyList<string> requestFields)
    {
        EnsureRequestCanFit(request);
        var normalizedMessages = ProviderTranscript.Normalize(
            request.Messages,
            _providerId,
            _apiId,
            request.Model,
            (id, _, _, _) => NormalizeChatToolCallId(id));
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Model,
            ["messages"] = ProjectMessages(request, normalizedMessages),
            ["stream"] = true,
        };
        if (_includeUsage && _protocol.SupportsUsageInStreaming)
        {
            payload["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true };
        }

        if (_protocol.SupportsStore)
        {
            payload["store"] = false;
        }

        var activeTools = ActiveTools(request, normalizedMessages);
        if (activeTools.Count > 0)
        {
            payload["tools"] = ProjectTools(activeTools);
            payload["tool_choice"] = "auto";
            if (_protocol.ZaiToolStream)
            {
                payload["tool_stream"] = true;
            }
        }

        if (request.Parameters.Temperature is { } temperature)
        {
            payload["temperature"] = temperature;
        }

        if (request.Parameters.MaxOutputTokens is { } maximum)
        {
            payload[_protocol.MaxTokensField == OpenAICompatibleMaxTokensField.MaxCompletionTokens
                ? "max_completion_tokens"
                : "max_tokens"] = maximum;
        }

        ApplyReasoningParameters(payload, request.Parameters);
        ApplyPromptCache(payload, request);

        foreach (var extension in request.Parameters.Extensions ?? new Dictionary<string, string>())
        {
            if (payload.ContainsKey(extension.Key))
            {
                throw new InvalidOperationException($"Model extension '{extension.Key}' cannot override a core request field.");
            }

            payload[extension.Key] = ParseExtension(extension.Value);
        }

        MergeSamplingParameters(payload, request.Parameters.SamplingParametersJson);

        requestFields = Array.AsReadOnly(payload.Keys
            .Where(IsSafeRequestFieldName)
            .OrderBy(field => field, StringComparer.Ordinal)
            .Take(64)
            .ToArray());

        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (body.Length > _maxRequestBytes)
        {
            throw new InvalidDataException("The model request exceeded the configured byte limit.");
        }

        return body;
    }

    private IReadOnlyList<ToolDefinition> ActiveTools(
        ModelRequest request,
        IReadOnlyList<AgentMessage> messages)
    {
        if (_protocol.DeferredToolsMode != OpenAICompatibleDeferredToolsMode.Kimi)
        {
            return request.Tools;
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var deferred = new HashSet<string>(StringComparer.Ordinal);
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
                        deferred.Add(name);
                    }
                }
            }
        }

        return request.Tools.Where(tool => !deferred.Contains(tool.Name)).ToArray();
    }

    private object[] ProjectTools(IEnumerable<ToolDefinition> tools) => tools.Select(ProjectTool).ToArray();

    private object ProjectTool(ToolDefinition tool)
    {
        if (tool.ConstrainedSampling?.Kind == ToolConstrainedSamplingKind.Grammar
            && _protocol.SupportsGrammarTools)
        {
            var grammar = !string.IsNullOrWhiteSpace(tool.ConstrainedSampling.OpenAiLark)
                ? (Syntax: "lark", Definition: tool.ConstrainedSampling.OpenAiLark!)
                : (Syntax: "regex", Definition: tool.ConstrainedSampling.OpenAiRegex!);
            _ = InferGrammarInputProperty(tool);
            return new Dictionary<string, object?>
            {
                ["type"] = "custom",
                ["custom"] = new Dictionary<string, object?>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["format"] = new Dictionary<string, object?>
                    {
                        ["type"] = "grammar",
                        ["grammar"] = new Dictionary<string, object?>
                        {
                            ["syntax"] = grammar.Syntax,
                            ["definition"] = grammar.Definition,
                        },
                    },
                },
            };
        }

        var strict = false;
        if (tool.ConstrainedSampling?.Kind == ToolConstrainedSamplingKind.JsonSchema)
        {
            if (!_protocol.SupportsStrictMode
                && tool.ConstrainedSampling.Strictness == ToolSchemaStrictness.Require)
            {
                throw new InvalidOperationException(
                    $"Tool '{tool.Name}' requires strict JSON-schema sampling, but the endpoint does not support it.");
            }

            strict = _protocol.SupportsStrictMode;
        }

        var function = new Dictionary<string, object?>
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["parameters"] = ParseElement(tool.InputSchemaJson),
        };
        if (_protocol.SupportsStrictMode)
        {
            function["strict"] = strict;
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = function,
        };
    }

    private static string InferGrammarInputProperty(ToolDefinition tool)
    {
        using var document = JsonDocument.Parse(tool.InputSchemaJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
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
            || properties.ValueKind != JsonValueKind.Object
            || !properties.TryGetProperty(property, out var schema)
            || schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("type", out var propertyType)
            || propertyType.ValueKind != JsonValueKind.String
            || propertyType.GetString() != "string")
        {
            throw new InvalidOperationException(
                $"Grammar tool '{tool.Name}' requires its sole required property to be declared as a string.");
        }

        return property;
    }

    private void ApplyReasoningParameters(IDictionary<string, object?> payload, ModelParameters parameters)
    {
        var effort = parameters.ReasoningLevel;
        var hasEffort = !string.IsNullOrWhiteSpace(effort);
        var enabled = hasEffort
                      && !string.Equals(effort, "none", StringComparison.OrdinalIgnoreCase)
                      && !string.Equals(effort, "off", StringComparison.OrdinalIgnoreCase)
                      && !string.Equals(effort, "disabled", StringComparison.OrdinalIgnoreCase);
        switch (_protocol.ThinkingFormat)
        {
            case OpenAICompatibleThinkingFormat.OpenRouter:
                if (hasEffort)
                {
                    payload["reasoning"] = new Dictionary<string, object?> { ["effort"] = effort };
                }
                break;
            case OpenAICompatibleThinkingFormat.AntLing:
                if (enabled)
                {
                    payload["reasoning"] = new Dictionary<string, object?> { ["effort"] = effort };
                }
                break;
            case OpenAICompatibleThinkingFormat.DeepSeek:
                payload["thinking"] = new Dictionary<string, object?> { ["type"] = enabled ? "enabled" : "disabled" };
                AddReasoningEffort(payload, enabled ? effort : null);
                break;
            case OpenAICompatibleThinkingFormat.Together:
                payload["reasoning"] = new Dictionary<string, object?> { ["enabled"] = enabled };
                AddReasoningEffort(payload, enabled ? effort : null);
                break;
            case OpenAICompatibleThinkingFormat.Baseten:
                AddTemplateValues(payload, "chat_template_args", _protocol.ChatTemplateArgumentsJson, effort, enabled);
                AddReasoningEffort(payload, hasEffort ? effort : null);
                break;
            case OpenAICompatibleThinkingFormat.Zai:
                payload["thinking"] = enabled
                    ? new Dictionary<string, object?> { ["type"] = "enabled", ["clear_thinking"] = false }
                    : new Dictionary<string, object?> { ["type"] = "disabled" };
                AddReasoningEffort(payload, enabled ? effort : null);
                break;
            case OpenAICompatibleThinkingFormat.Qwen:
                payload["enable_thinking"] = enabled;
                AddReasoningEffort(payload, enabled ? effort : null);
                break;
            case OpenAICompatibleThinkingFormat.ChatTemplate:
                AddTemplateValues(payload, "chat_template_kwargs", _protocol.ChatTemplateKeywordArgumentsJson, effort, enabled);
                break;
            case OpenAICompatibleThinkingFormat.QwenChatTemplate:
                payload["chat_template_kwargs"] = new Dictionary<string, object?>
                {
                    ["enable_thinking"] = enabled,
                    ["preserve_thinking"] = true,
                };
                break;
            case OpenAICompatibleThinkingFormat.StringThinking:
                payload["thinking"] = hasEffort ? effort : "disabled";
                break;
            default:
                // "off" and "disabled" are provider-neutral control values, not portable
                // OpenAI reasoning_effort values. Known providers use an explicit thinking
                // format above; an unconfigured compatible endpoint must not receive an
                // invented value that can turn a bounded request into HTTP 400.
                AddReasoningEffort(
                    payload,
                    string.Equals(effort, "off", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(effort, "disabled", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : effort);
                break;
        }

        if (_protocol.SupportsThinkingTokenBudget
            && enabled
            && parameters.ReasoningBudgets.TryGetValue(effort!, out var budget))
        {
            payload["thinking_token_budget"] = budget;
        }
    }

    private static string ClassifyHttpFailure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "invalid-request",
        HttpStatusCode.Unauthorized => "authentication",
        HttpStatusCode.Forbidden => "permission",
        HttpStatusCode.NotFound => "not-found",
        HttpStatusCode.RequestTimeout => "timeout",
        HttpStatusCode.Conflict => "conflict",
        HttpStatusCode.RequestEntityTooLarge => "request-too-large",
        HttpStatusCode.UnsupportedMediaType => "unsupported-media",
        HttpStatusCode.TooManyRequests => "rate-limit",
        _ when (int)statusCode >= 500 => "server",
        _ when (int)statusCode >= 400 => "client",
        _ => "unknown",
    };

    private static void AddTemplateValues(
        IDictionary<string, object?> payload,
        string field,
        string? json,
        string? effort,
        bool enabled)
    {
        if (json is null)
        {
            return;
        }

        using var document = JsonDocument.Parse(json);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                values[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number when property.Value.TryGetInt64(out var integer) => integer,
                    JsonValueKind.Number => property.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => throw new InvalidOperationException("A chat-template value must be a scalar or variable."),
                };
                continue;
            }

            if (!property.Value.TryGetProperty("$var", out var variable)
                || variable.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("A chat-template variable is invalid.");
            }

            var omitWhenOff = property.Value.TryGetProperty("omitWhenOff", out var omit)
                              && omit.ValueKind == JsonValueKind.True;
            if (!enabled && omitWhenOff)
            {
                continue;
            }

            object? resolved = variable.GetString() switch
            {
                "thinking.enabled" => enabled,
                "thinking.effort" => effort,
                var name => throw new InvalidOperationException($"Unknown chat-template variable '{name}'."),
            };
            if (resolved is not null)
            {
                values[property.Name] = resolved;
            }
        }

        if (values.Count > 0)
        {
            payload[field] = values;
        }
    }

    private void AddReasoningEffort(IDictionary<string, object?> payload, string? effort)
    {
        if (_protocol.SupportsReasoningEffort && !string.IsNullOrWhiteSpace(effort))
        {
            payload["reasoning_effort"] = effort;
        }
    }

    private static bool IsSafeRequestFieldName(string field) =>
        field.Length is > 0 and <= 64
        && field.All(character => character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_' or '-' or '.');

    private static string? ReadProviderRequestId(HttpResponseMessage response)
    {
        foreach (var name in new[] { "x-request-id", "request-id", "x-trace-id" })
        {
            if (!response.Headers.TryGetValues(name, out var values))
            {
                continue;
            }

            var value = values.FirstOrDefault();
            if (value is { Length: > 0 and <= 256 }
                && value.All(character => character is >= 'a' and <= 'z'
                    or >= 'A' and <= 'Z'
                    or >= '0' and <= '9'
                    or '-' or '_' or '.' or ':' or '/'))
            {
                return value;
            }
        }

        return null;
    }

    private void ApplyPromptCache(IDictionary<string, object?> payload, ModelRequest request)
    {
        if (request.Parameters.CacheRetention == ModelCacheRetention.None)
        {
            return;
        }

        var supportsPromptCacheKey = _endpoint.Host.EndsWith("api.openai.com", StringComparison.OrdinalIgnoreCase)
                                     || (request.Parameters.CacheRetention == ModelCacheRetention.Long
                                         && _protocol.SupportsLongCacheRetention);
        if (supportsPromptCacheKey && request.SessionId is { } sessionId)
        {
            payload["prompt_cache_key"] = sessionId.Length <= 64 ? sessionId : sessionId.Substring(0, 64);
        }

        if (request.Parameters.CacheRetention == ModelCacheRetention.Long
            && _protocol.SupportsLongCacheRetention)
        {
            payload["prompt_cache_retention"] = "24h";
        }
    }

    private static void MergeSamplingParameters(IDictionary<string, object?> payload, string? json)
    {
        if (json is null)
        {
            return;
        }

        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            payload[property.Name] = property.Value.Clone();
        }
    }

    private void EnsureRequestCanFit(ModelRequest request)
    {
        var lowerBound = 32L;

        void AddBytes(long value)
        {
            lowerBound = checked(lowerBound + value);
            if (lowerBound > _maxRequestBytes)
            {
                throw new InvalidDataException("The model request exceeded the configured byte limit.");
            }
        }

        void AddString(string? value)
        {
            if (value is not null)
            {
                AddBytes(Encoding.UTF8.GetByteCount(value));
            }
        }

        AddString(request.Model);
        AddString(request.SystemPrompt);
        foreach (var message in request.Messages)
        {
            AddBytes(20);
            AddString(message.ToolCallId);
            if (message.Role == AgentRole.Custom)
            {
                AddString(message.CustomRole);
                AddBytes(3);
            }

            var joinedParts = 0;
            foreach (var part in message.Content)
            {
                if (message.Role == AgentRole.Assistant && part is ToolCallContent assistantCall)
                {
                    AddBytes(48);
                    AddString(assistantCall.Id);
                    AddString(assistantCall.Name);
                    AddString(assistantCall.ArgumentsJson);
                    continue;
                }

                switch (part)
                {
                    case TextContent text:
                        AddString(text.Text);
                        joinedParts++;
                        break;
                    case JsonContent json:
                        AddString(json.Json);
                        joinedParts++;
                        break;
                    case ResourceContent resource:
                        AddBytes(32);
                        AddString(resource.Name);
                        AddString(resource.MediaType);
                        AddString(resource.Uri);
                        joinedParts++;
                        break;
                    case BinaryContent binary:
                        AddBytes(32);
                        AddString(binary.Name);
                        AddString(binary.MediaType);
                        AddString(binary.Data);
                        joinedParts++;
                        break;
                    case ToolCallContent genericCall:
                        AddBytes(16);
                        AddString(genericCall.Name);
                        AddString(genericCall.ArgumentsJson);
                        joinedParts++;
                        break;
                }
            }

            if (joinedParts > 1)
            {
                AddBytes(joinedParts - 1L);
            }
        }

        foreach (var tool in request.Tools)
        {
            AddBytes(64);
            AddString(tool.Name);
            AddString(tool.Description);
            AddString(tool.InputSchemaJson);
        }

        AddString(request.Parameters.ReasoningLevel);
        foreach (var extension in request.Parameters.Extensions ?? new Dictionary<string, string>())
        {
            AddString(extension.Key);
            AddString(extension.Value);
        }
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private IReadOnlyList<object> ProjectMessages(
        ModelRequest request,
        IReadOnlyList<AgentMessage> messages)
    {
        var projected = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["role"] = _protocol.SupportsDeveloperRole ? "developer" : "system",
                ["content"] = request.SystemPrompt,
            },
        };
        var lastWasToolResult = false;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role != AgentRole.Tool)
            {
                if (lastWasToolResult
                    && message.Role is AgentRole.User or AgentRole.Custom
                    && _protocol.RequiresAssistantAfterToolResult)
                {
                    projected.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = "I have processed the tool results.",
                    });
                }

                projected.Add(ProjectMessage(message));
                lastWasToolResult = false;
                continue;
            }

            var attachments = new List<object>();
            var addedToolNames = new HashSet<string>(StringComparer.Ordinal);
            while (index < messages.Count && messages[index].Role == AgentRole.Tool)
            {
                var toolMessage = messages[index];
                projected.Add(ProjectMessage(toolMessage));
                foreach (var resource in toolMessage.Content.OfType<ResourceContent>())
                {
                    var attachment = ProjectNativeResource(resource);
                    if (attachment is not null)
                    {
                        attachments.Add(attachment);
                    }
                }

                foreach (var binary in toolMessage.Content.OfType<BinaryContent>())
                {
                    var attachment = ProjectNativeBinary(binary);
                    if (attachment is not null)
                    {
                        attachments.Add(attachment);
                    }
                }

                foreach (var name in toolMessage.AddedToolNames)
                {
                    addedToolNames.Add(name);
                }

                index++;
            }

            index--;
            if (attachments.Count > 0)
            {
                if (_protocol.RequiresAssistantAfterToolResult)
                {
                    projected.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = "I have processed the tool results.",
                    });
                }

                var content = new List<object>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = "Attached resource(s) returned by the preceding tool results:",
                    },
                };
                content.AddRange(attachments);
                projected.Add(new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = content,
                });
                lastWasToolResult = false;
            }
            else
            {
                lastWasToolResult = true;
            }

            if (_protocol.DeferredToolsMode == OpenAICompatibleDeferredToolsMode.Kimi
                && addedToolNames.Count > 0)
            {
                var loaded = request.Tools.Where(tool => addedToolNames.Contains(tool.Name)).ToArray();
                if (loaded.Length > 0)
                {
                    projected.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "system",
                        ["tools"] = ProjectTools(loaded),
                    });
                }
            }
        }

        return projected;
    }

    private object ProjectMessage(AgentMessage message)
    {
        if (message.Role == AgentRole.Assistant)
        {
            var reasoning = message.Content.OfType<ReasoningContent>()
                .Where(content => !string.IsNullOrWhiteSpace(content.Text))
                .ToArray();
            var assistantContent = _protocol.RequiresThinkingAsText && reasoning.Length > 0
                ? string.Join("\n\n", reasoning.Select(content => content.Text)
                    .Concat(new[] { JoinContent(message.Content.Where(content => content is not ToolCallContent)) })
                    .Where(text => text.Length > 0))
                : JoinContent(message.Content.Where(content => content is not ToolCallContent));
            var assistant = new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = assistantContent,
            };
            var calls = message.Content.OfType<ToolCallContent>().Select(call => new Dictionary<string, object?>
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = call.Name,
                    ["arguments"] = call.ArgumentsJson,
                },
            }).ToArray();
            if (calls.Length > 0)
            {
                assistant["tool_calls"] = calls;
            }

            foreach (var signedReasoning in message.Content
                         .OfType<ReasoningContent>()
                         .Where(content => !string.IsNullOrWhiteSpace(content.Signature))
                         .GroupBy(content => content.Signature!, StringComparer.Ordinal))
            {
                if (_protocol.RequiresThinkingAsText)
                {
                    break;
                }

                if (assistant.ContainsKey(signedReasoning.Key))
                {
                    throw new InvalidDataException("A reasoning signature cannot override a core assistant message field.");
                }

                assistant[signedReasoning.Key] = string.Join("\n", signedReasoning.Select(content => content.Text));
            }

            if (_protocol.RequiresReasoningContentOnAssistantMessages
                && !assistant.ContainsKey("reasoning_content"))
            {
                assistant["reasoning_content"] = string.Empty;
            }

            return assistant;
        }

        if (message.Role == AgentRole.Tool)
        {
            var toolResult = new Dictionary<string, object?>
            {
                ["role"] = "tool",
                ["tool_call_id"] = message.ToolCallId,
                ["content"] = JoinContent(message.Content),
            };
            if (_protocol.RequiresToolResultName)
            {
                toolResult["name"] = message.ToolName;
            }

            return toolResult;
        }

        const string role = "user";
        object content = ProjectUserContent(message);
        if (message.Role == AgentRole.Custom)
        {
            content = PrefixCustomRole(content, message.CustomRole!);
        }

        return new Dictionary<string, object?>
        {
            ["role"] = role,
            ["content"] = content,
        };
    }

    private object ProjectUserContent(AgentMessage message)
    {
        var visible = message.Content.Where(part => part is not ReasoningContent and not ToolCallContent).ToArray();
        if (!visible.Any(part => part is ResourceContent or BinaryContent))
        {
            return JoinContent(visible);
        }

        var parts = new List<object>();
        foreach (var part in visible)
        {
            if (part is ResourceContent resource)
            {
                parts.Add(ProjectResource(resource));
                continue;
            }

            if (part is BinaryContent binary)
            {
                parts.Add(ProjectBinary(binary));
                continue;
            }

            var text = ContentText(part);
            if (text.Length > 0)
            {
                parts.Add(new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = text,
                });
            }
        }

        return parts;
    }

    private object ProjectResource(ResourceContent resource)
    {
        return ProjectNativeResource(resource) ?? new Dictionary<string, object?>
        {
            ["type"] = "text",
            ["text"] = ResourceText(resource),
        };
    }

    private object ProjectBinary(BinaryContent binary)
    {
        return ProjectNativeBinary(binary) ?? new Dictionary<string, object?>
        {
            ["type"] = "text",
            ["text"] = BinaryText(binary),
        };
    }

    private static object? ProjectNativeBinary(BinaryContent binary)
    {
        if (binary.MediaKind == AgentMediaKind.Image
            || binary.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object?>
            {
                ["type"] = "image_url",
                ["image_url"] = new Dictionary<string, object?>
                {
                    ["url"] = $"data:{binary.MediaType};base64,{binary.Data}",
                },
            };
        }

        return null;
    }

    private object? ProjectNativeResource(ResourceContent resource)
    {
        var custom = _projectResourcePart?.Invoke(resource);
        if (custom is not null)
        {
            using var document = JsonDocument.Parse(custom);
            EnsureUnambiguous(document.RootElement, "A projected resource part contains duplicate JSON property names.");
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("A projected resource part must be a JSON object.");
            }

            return document.RootElement.Clone();
        }

        if (resource.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object?>
            {
                ["type"] = "image_url",
                ["image_url"] = new Dictionary<string, object?>
                {
                    ["url"] = resource.Uri,
                },
            };
        }

        return null;
    }

    private static object PrefixCustomRole(object content, string customRole)
    {
        var prefix = "[" + customRole + "]";
        if (content is string text)
        {
            return prefix + "\n" + text;
        }

        if (content is not IEnumerable<object> existing)
        {
            throw new InvalidDataException("A custom-role message produced an unsupported content projection.");
        }

        return new object[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = prefix,
            },
        }.Concat(existing).ToArray();
    }

    private static string JoinContent(IEnumerable<AgentContent> content)
    {
        return string.Join("\n", content.Where(part => part is not ReasoningContent).Select(part => part switch
        {
            _ => ContentText(part),
        }));
    }

    private static string ContentText(AgentContent content) => content switch
    {
        TextContent text => text.Text,
        JsonContent json => json.Json,
        ResourceContent resource => ResourceText(resource),
        BinaryContent binary => BinaryText(binary),
        ToolCallContent call => $"[tool_call {call.Name}] {call.ArgumentsJson}",
        _ => string.Empty,
    };

    private static string ResourceText(ResourceContent resource) =>
        $"[resource name={resource.Name ?? "unnamed"} media_type={resource.MediaType}] {resource.Uri}";

    private static string BinaryText(BinaryContent binary) =>
        $"[binary name={binary.Name ?? "unnamed"} media_type={binary.MediaType} data_omitted]";

    private static string NormalizeChatToolCallId(string id)
    {
        var pieces = id.Split(new[] { '|' }, 2);
        var callId = SanitizeId(pieces[0]);
        var combined = pieces.Length == 2 && pieces[1].Length > 0
            ? callId + "_" + SanitizeId(pieces[1])
            : callId;
        if (combined.Length <= 40)
        {
            return combined;
        }

        var hash = ShortHash(id).Substring(0, 8);
        return combined.Substring(0, Math.Max(1, 40 - hash.Length - 1)) + "_" + hash;
    }

    private static string SanitizeId(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_');
        }

        return builder.ToString();
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

    private static object? ParseExtension(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            EnsureUnambiguous(document.RootElement, "A model extension contains duplicate JSON property names.");
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static void EnsureUnambiguous(JsonElement value, string message)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(message);
                }

                EnsureUnambiguous(property.Value, message);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                EnsureUnambiguous(item, message);
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(
        HttpContent content,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var cancellationRegistration = cancellationToken.Register(stream.Dispose);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
        var buffer = ArrayPool<char>.Shared.Rent(Math.Min(4096, maximumCharacters));
        try
        {
            var builder = new StringBuilder();
            while (builder.Length < maximumCharacters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await reader.ReadAsync(
                    buffer,
                    0,
                    Math.Min(buffer.Length, maximumCharacters - builder.Length)).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                builder.Append(buffer, 0, read);
            }

            return builder.ToString();
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static async IAsyncEnumerable<string> ReadBoundedLinesAsync(
        StreamReader reader,
        int maximumCharacters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<char>.Shared.Rent(Math.Min(4096, maximumCharacters + 1));
        var line = new StringBuilder(Math.Min(4096, maximumCharacters));
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
                        continue;
                    }

                    line.Append(buffer[index]);
                    if (line.Length > maximumCharacters)
                    {
                        throw new InvalidDataException("A model stream event exceeded the configured size limit.");
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

    private sealed class StreamState
    {
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _reasoning = new();
        private readonly SortedDictionary<int, ToolBuilder> _tools = new();
        private readonly List<ContentSlot> _contentOrder = new();
        private ModelStopReason _stopReason = ModelStopReason.Stop;
        private ModelUsage _usage = new();
        private string? _errorMessage;
        private bool _textStarted;
        private bool _reasoningStarted;
        private bool _contentEnded;
        private bool _hasFinishReason;
        private readonly int _maximumCharacters;
        private readonly int _maximumToolCalls;
        private readonly IReadOnlyList<string> _reasoningDeltaFields;
        private readonly string _requestModel;
        private readonly string _providerId;
        private readonly string _apiId;
        private string? _reasoningSignature;
        private string? _responseModel;
        private string? _responseId;
        private string? _rawStopReason;
        private long _characters;

        public StreamState(
            string model,
            string providerId,
            string apiId,
            int maximumCharacters,
            int maximumToolCalls,
            IReadOnlyList<string> reasoningDeltaFields)
        {
            _requestModel = model;
            _providerId = providerId;
            _apiId = apiId;
            _maximumCharacters = maximumCharacters;
            _maximumToolCalls = maximumToolCalls;
            _reasoningDeltaFields = reasoningDeltaFields;
        }

        public IReadOnlyList<ModelStreamEvent> Apply(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
                var root = document.RootElement;
                RequireKind(root, JsonValueKind.Object, "A model stream event must be a JSON object.");
                EnsureUnambiguous(root, "The model stream contains duplicate JSON property names.");
                ReadResponseIdentity(root);
                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.ValueKind == JsonValueKind.Object
                        && error.TryGetProperty("message", out var messageElement)
                        && messageElement.ValueKind == JsonValueKind.String
                            ? messageElement.GetString()
                            : error.GetRawText();
                    throw new InvalidDataException("The model stream returned an error: " + message);
                }

                ReadUsage(root);
                if (!root.TryGetProperty("choices", out var choices))
                {
                    return Array.Empty<ModelStreamEvent>();
                }

                RequireKind(choices, JsonValueKind.Array, "Model stream choices must be an array.");
                if (choices.GetArrayLength() == 0)
                {
                    return Array.Empty<ModelStreamEvent>();
                }

                if (choices.GetArrayLength() > 1)
                {
                    throw new InvalidDataException("The model stream returned multiple choices, but only one response was requested.");
                }

                var choice = choices[0];
                RequireKind(choice, JsonValueKind.Object, "Each model stream choice must be an object.");
                if (_hasFinishReason)
                {
                    throw new InvalidDataException("The model stream emitted another choice after its finish reason.");
                }

                ReadFinishReason(choice);
                var updates = new List<ModelStreamEvent>();
                if (choice.TryGetProperty("delta", out var delta))
                {
                    RequireKind(delta, JsonValueKind.Object, "A model stream delta must be an object.");
                    ApplyReasoning(delta, updates);
                    ApplyText(delta, "content", _text, ref _textStarted, ModelStreamEventKind.TextStarted, ModelStreamEventKind.TextDelta, updates);
                    if (delta.TryGetProperty("tool_calls", out var calls))
                    {
                        ApplyToolCalls(calls, updates);
                    }
                }

                if (_hasFinishReason && !_contentEnded)
                {
                    AddEndedEvents(updates);
                    _contentEnded = true;
                }

                return updates;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The model stream contained invalid JSON.", exception);
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidDataException("The model stream did not match the expected response shape.", exception);
            }
        }

        private void ApplyToolCalls(JsonElement calls, ICollection<ModelStreamEvent> updates)
        {
            RequireKind(calls, JsonValueKind.Array, "Model tool calls must be an array.");
            foreach (var call in calls.EnumerateArray())
            {
                RequireKind(call, JsonValueKind.Object, "Each model tool call must be an object.");
                int? explicitIndex = null;
                if (call.TryGetProperty("index", out var indexElement))
                {
                    if (!indexElement.TryGetInt32(out var parsedIndex))
                    {
                        throw new InvalidDataException("A model tool call index must be an integer.");
                    }

                    explicitIndex = parsedIndex;
                }

                string? incomingId = null;
                if (call.TryGetProperty("id", out var incomingIdElement))
                {
                    RequireKind(incomingIdElement, JsonValueKind.String, "A model tool call ID must be a string.");
                    incomingId = incomingIdElement.GetString();
                }

                var index = ResolveToolIndex(explicitIndex, incomingId);

                if (index < 0 || index >= _maximumToolCalls)
                {
                    throw new InvalidDataException("A model tool call used a negative index or exceeded the configured tool call limit.");
                }

                var created = false;
                if (!_tools.TryGetValue(index, out var builder))
                {
                    if (_tools.Count >= _maximumToolCalls)
                    {
                        throw new InvalidDataException("The model response exceeded the configured tool call limit.");
                    }

                    builder = new ToolBuilder();
                    _tools.Add(index, builder);
                    _contentOrder.Add(new ContentSlot(ContentSlotKind.Tool, index));
                    created = true;
                }

                if (incomingId is not null)
                {
                    if (builder.Id is not null && !string.Equals(builder.Id, incomingId, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("A streamed model tool call changed its ID.");
                    }

                    AddCharacters(incomingId.Length);
                    builder.Id = incomingId;
                }

                if (call.TryGetProperty("function", out var function))
                {
                    RequireKind(function, JsonValueKind.Object, "A model tool call function must be an object.");
                    if (function.TryGetProperty("name", out var name))
                    {
                        RequireKind(name, JsonValueKind.String, "A model tool name must be a string.");
                        var nameText = name.GetString()!;
                        AddCharacters(nameText.Length);
                        builder.Name.Append(nameText);
                    }

                    if (created)
                    {
                        updates.Add(ModelStreamEvent.Update(
                            ModelStreamEventKind.ToolCallStarted,
                            Partial(),
                            contentIndex: ContentIndex(ContentSlotKind.Tool, index),
                            toolCallId: builder.Id,
                            toolName: builder.Name.Length == 0 ? null : builder.Name.ToString()));
                    }

                    if (function.TryGetProperty("arguments", out var arguments))
                    {
                        RequireKind(arguments, JsonValueKind.String, "Model tool arguments must be streamed as a string.");
                        var argumentText = arguments.GetString()!;
                        AddCharacters(argumentText.Length);
                        builder.Arguments.Append(argumentText);
                        updates.Add(ModelStreamEvent.Update(
                            ModelStreamEventKind.ToolCallDelta,
                            Partial(),
                            argumentText,
                            ContentIndex(ContentSlotKind.Tool, index),
                            builder.Id,
                            builder.Name.Length == 0 ? null : builder.Name.ToString()));
                    }
                }
                else if (created)
                {
                    updates.Add(ModelStreamEvent.Update(
                        ModelStreamEventKind.ToolCallStarted,
                        Partial(),
                        contentIndex: ContentIndex(ContentSlotKind.Tool, index),
                        toolCallId: builder.Id));
                }
            }
        }

        private int ResolveToolIndex(int? explicitIndex, string? incomingId)
        {
            if (explicitIndex is { } index)
            {
                return index;
            }

            if (!string.IsNullOrEmpty(incomingId))
            {
                foreach (var pair in _tools)
                {
                    if (string.Equals(pair.Value.Id, incomingId, StringComparison.Ordinal))
                    {
                        return pair.Key;
                    }
                }

                var candidate = 0;
                while (_tools.ContainsKey(candidate))
                {
                    candidate++;
                }

                return candidate;
            }

            if (_tools.Count == 0)
            {
                return 0;
            }

            if (_tools.Count == 1)
            {
                return _tools.Keys.Single();
            }

            throw new InvalidDataException("A model tool call delta omitted both index and ID while multiple tool calls were active.");
        }

        private void AddEndedEvents(ICollection<ModelStreamEvent> updates)
        {
            foreach (var slot in _contentOrder)
            {
                var contentIndex = _contentOrder.IndexOf(slot);
                switch (slot.Kind)
                {
                    case ContentSlotKind.Reasoning:
                        updates.Add(ModelStreamEvent.Update(
                            ModelStreamEventKind.ReasoningEnded,
                            Partial(),
                            contentIndex: contentIndex,
                            content: _reasoning.ToString()));
                        break;
                    case ContentSlotKind.Text:
                        updates.Add(ModelStreamEvent.Update(
                            ModelStreamEventKind.TextEnded,
                            Partial(),
                            contentIndex: contentIndex,
                            content: _text.ToString()));
                        break;
                    case ContentSlotKind.Tool:
                        var tool = _tools[slot.ToolIndex];
                        var toolCall = CreateToolCall(slot.ToolIndex, tool, _stopReason);
                        updates.Add(ModelStreamEvent.Update(
                            ModelStreamEventKind.ToolCallEnded,
                            Partial(),
                            contentIndex: contentIndex,
                            toolCall: toolCall));
                        break;
                }
            }
        }

        public ModelResponse Partial() => new(
            CurrentContent(includeTools: true, ModelStopReason.Pending),
            ModelStopReason.Pending,
            _usage,
            provider: _providerId,
            api: _apiId,
            responseModel: _responseModel ?? _requestModel,
            responseId: _responseId,
            rawStopReason: _rawStopReason);

        public bool HasFinishReason => _hasFinishReason;

        public ModelResponse Complete()
        {
            if (_stopReason == ModelStopReason.ToolUse
                && _tools.Any(pair => string.IsNullOrWhiteSpace(pair.Value.Id)
                                      || pair.Value.Name.Length == 0))
            {
                throw new InvalidDataException("A completed model tool call is missing its ID or function name.");
            }

            var content = CurrentContent(includeTools: true, _stopReason);
            return new ModelResponse(
                content,
                _stopReason,
                _usage,
                _errorMessage,
                _providerId,
                _apiId,
                _responseModel ?? _requestModel,
                _responseId,
                _rawStopReason);
        }

        public void InferStopReason()
        {
            if (_hasFinishReason)
            {
                return;
            }

            _stopReason = _tools.Count > 0 ? ModelStopReason.ToolUse : ModelStopReason.Stop;
            _rawStopReason = null;
            _hasFinishReason = true;
            _contentEnded = true;
        }

        private IReadOnlyList<AgentContent> CurrentContent(bool includeTools, ModelStopReason reason)
        {
            var content = new List<AgentContent>();
            foreach (var slot in _contentOrder)
            {
                switch (slot.Kind)
                {
                    case ContentSlotKind.Reasoning:
                        content.Add(new ReasoningContent(_reasoning.ToString(), _reasoningSignature));
                        break;
                    case ContentSlotKind.Text:
                        content.Add(new TextContent(_text.ToString()));
                        break;
                    case ContentSlotKind.Tool when includeTools:
                        content.Add(CreateToolCall(slot.ToolIndex, _tools[slot.ToolIndex], reason));
                        break;
                }
            }

            return content;
        }

        private static ToolCallContent CreateToolCall(
            int index,
            ToolBuilder tool,
            ModelStopReason reason)
        {
            var arguments = tool.Arguments.Length == 0 ? "{}" : tool.Arguments.ToString();
            if (reason == ModelStopReason.Pending)
            {
                arguments = StreamingJson.ParseObject(arguments);
            }

            if (reason == ModelStopReason.Length && !IsJsonObject(arguments))
            {
                arguments = StreamingJson.ParseObject(arguments);
            }

            return new ToolCallContent(
                string.IsNullOrWhiteSpace(tool.Id) ? "call_" + index : tool.Id,
                tool.Name.Length == 0 ? "unknown_tool" : tool.Name.ToString(),
                arguments);
        }

        private void ApplyText(
            JsonElement delta,
            string property,
            StringBuilder builder,
            ref bool started,
            ModelStreamEventKind startedKind,
            ModelStreamEventKind deltaKind,
            ICollection<ModelStreamEvent> updates)
        {
            if (!delta.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            RequireKind(value, JsonValueKind.String, $"Model delta field '{property}' must be a string or null.");

            var text = value.GetString();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (!started)
            {
                started = true;
                var slotKind = startedKind == ModelStreamEventKind.ReasoningStarted
                    ? ContentSlotKind.Reasoning
                    : ContentSlotKind.Text;
                _contentOrder.Add(new ContentSlot(slotKind));
                updates.Add(ModelStreamEvent.Update(
                    startedKind,
                    Partial(),
                    contentIndex: ContentIndex(slotKind)));
            }

            AddCharacters(text.Length);
            builder.Append(text);
            var contentKind = deltaKind == ModelStreamEventKind.ReasoningDelta
                ? ContentSlotKind.Reasoning
                : ContentSlotKind.Text;
            updates.Add(ModelStreamEvent.Update(
                deltaKind,
                Partial(),
                text,
                ContentIndex(contentKind)));
        }

        private int ContentIndex(ContentSlotKind kind, int toolIndex = -1)
        {
            for (var index = 0; index < _contentOrder.Count; index++)
            {
                var slot = _contentOrder[index];
                if (slot.Kind == kind && (kind != ContentSlotKind.Tool || slot.ToolIndex == toolIndex))
                {
                    return index;
                }
            }

            throw new InvalidDataException("A streamed content block was not registered in response order.");
        }

        private void ApplyReasoning(JsonElement delta, ICollection<ModelStreamEvent> updates)
        {
            foreach (var property in _reasoningDeltaFields)
            {
                if (!delta.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (_reasoningSignature is not null
                    && !string.Equals(_reasoningSignature, property, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("A model stream changed its reasoning delta field during one response.");
                }

                _reasoningSignature = property;
                ApplyText(
                    delta,
                    property,
                    _reasoning,
                    ref _reasoningStarted,
                    ModelStreamEventKind.ReasoningStarted,
                    ModelStreamEventKind.ReasoningDelta,
                    updates);
                return;
            }
        }

        private void ReadFinishReason(JsonElement choice)
        {
            if (!choice.TryGetProperty("finish_reason", out var reason) || reason.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            RequireKind(reason, JsonValueKind.String, "A model finish reason must be a string or null.");
            _hasFinishReason = true;
            _rawStopReason = reason.GetString();
            _stopReason = _rawStopReason switch
            {
                "tool_calls" or "function_call" => ModelStopReason.ToolUse,
                "length" => ModelStopReason.Length,
                "stop" or "end" => ModelStopReason.Stop,
                "content_filter" => SetError("The provider stopped the response because of its content filter."),
                "network_error" => SetError("The provider stopped the response because of a network error."),
                var unknown => SetError("The model stopped with unsupported finish reason '" + unknown + "'."),
            };
        }

        private void ReadResponseIdentity(JsonElement root)
        {
            ReadStableString(root, "id", ref _responseId, "response ID");
            ReadStableString(root, "model", ref _responseModel, "response model");
        }

        private static void ReadStableString(
            JsonElement root,
            string property,
            ref string? destination,
            string label)
        {
            if (!root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            RequireKind(value, JsonValueKind.String, $"The model {label} must be a string.");
            var incoming = value.GetString();
            if (string.IsNullOrWhiteSpace(incoming))
            {
                throw new InvalidDataException($"The model {label} cannot be empty.");
            }

            if (destination is not null && !string.Equals(destination, incoming, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The model stream changed its {label}.");
            }

            destination = incoming;
        }

        private ModelStopReason SetError(string message)
        {
            _errorMessage = message;
            return ModelStopReason.Error;
        }

        private void ReadUsage(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            RequireKind(usage, JsonValueKind.Object, "Model usage must be an object or null.");
            var prompt = ReadNonNegativeLong(usage, "prompt_tokens");
            var output = ReadNonNegativeLong(usage, "completion_tokens");
            var cached = 0L;
            var cacheWrite = 0L;
            var reasoning = 0L;
            if (usage.TryGetProperty("prompt_tokens_details", out var details))
            {
                if (details.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Model usage token details must be an object.");
                }

                cached = ReadNonNegativeLong(details, "cached_tokens");
                cacheWrite = ReadNonNegativeLong(details, "cache_write_tokens");
            }

            if (cached == 0)
            {
                cached = ReadNonNegativeLong(usage, "prompt_cache_hit_tokens");
            }

            if (usage.TryGetProperty("completion_tokens_details", out var completionDetails))
            {
                if (completionDetails.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Model completion token details must be an object.");
                }

                reasoning = ReadNonNegativeLong(completionDetails, "reasoning_tokens");
            }

            if (cached + cacheWrite > prompt)
            {
                throw new InvalidDataException("Cached and cache-written prompt tokens cannot exceed total prompt tokens.");
            }

            if (reasoning > output)
            {
                throw new InvalidDataException("Reasoning tokens cannot exceed completion tokens.");
            }

            _usage = new ModelUsage(prompt - cached - cacheWrite, output, cached, cacheWrite, reasoning);
        }

        private static long ReadNonNegativeLong(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out var value))
            {
                return 0;
            }

            if (!value.TryGetInt64(out var number) || number < 0)
            {
                throw new InvalidDataException($"Model usage field '{property}' must be a non-negative integer.");
            }

            return number;
        }

        private static void RequireKind(JsonElement value, JsonValueKind expected, string message)
        {
            if (value.ValueKind != expected)
            {
                throw new InvalidDataException(message);
            }
        }

        private static bool IsJsonObject(string value)
        {
            try
            {
                using var document = JsonDocument.Parse(value);
                return document.RootElement.ValueKind == JsonValueKind.Object;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private void AddCharacters(int count)
        {
            _characters += count;
            if (_characters > _maximumCharacters)
            {
                throw new InvalidDataException("The accumulated model response exceeded the configured size limit.");
            }
        }

        private sealed class ToolBuilder
        {
            public string? Id { get; set; }

            public StringBuilder Name { get; } = new();

            public StringBuilder Arguments { get; } = new();
        }

        private enum ContentSlotKind
        {
            Reasoning,
            Text,
            Tool,
        }

        private sealed class ContentSlot
        {
            public ContentSlot(ContentSlotKind kind, int toolIndex = -1)
            {
                Kind = kind;
                ToolIndex = toolIndex;
            }

            public ContentSlotKind Kind { get; }

            public int ToolIndex { get; }
        }
    }
}
