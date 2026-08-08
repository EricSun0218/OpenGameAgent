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
        IReadOnlyDictionary<string, string>? headers = null)
    {
        ApiKey = apiKey;
        Headers = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase));
    }

    public string? ApiKey { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }
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

    public IDictionary<string, string> Headers { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

    public OpenAISessionAffinityFormat SessionAffinityFormat { get; set; } = OpenAISessionAffinityFormat.OpenAI;

    public int MaxEventCharacters { get; set; } = 4_000_000;

    public int MaxErrorCharacters { get; set; } = 64_000;

    public int MaxRequestBytes { get; set; } = 16_000_000;

    public int MaxResponseCharacters { get; set; } = 16_000_000;

    public int MaxToolCallsPerResponse { get; set; } = 256;
}

public sealed class OpenAIResponsesProvider : IModelProvider, IModelProviderCapabilities
{
    private const int MinimumOutputTokens = 16;
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
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly string _providerId;
    private readonly string _apiId;
    private readonly bool _supportsDeveloperRole;
    private readonly bool _supportsStrictTools;
    private readonly bool _supportsGrammarTools;
    private readonly bool _supportsAdditionalTools;
    private readonly bool _supportsToolSearch;
    private readonly bool _supportsExplicitPromptCacheMode;
    private readonly bool _supportsLongCacheRetention;
    private readonly OpenAISessionAffinityFormat _sessionAffinityFormat;
    private readonly int _maxEventCharacters;
    private readonly int _maxErrorCharacters;
    private readonly int _maxRequestBytes;
    private readonly int _maxResponseCharacters;
    private readonly int _maxToolCallsPerResponse;
    private readonly IReadOnlyCollection<string> _supportedApis;

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
        _headers = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(options.Headers, StringComparer.OrdinalIgnoreCase));
        _providerId = options.ProviderId;
        _apiId = options.ApiId;
        _supportsDeveloperRole = options.SupportsDeveloperRole;
        _supportsStrictTools = options.SupportsStrictTools;
        _supportsGrammarTools = options.SupportsGrammarTools;
        _supportsAdditionalTools = options.SupportsAdditionalTools;
        _supportsToolSearch = options.SupportsToolSearch;
        _supportsExplicitPromptCacheMode = options.SupportsExplicitPromptCacheMode;
        _supportsLongCacheRetention = options.SupportsLongCacheRetention;
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

        if (request.Parameters.Transport is ModelTransport.WebSocket or ModelTransport.CachedWebSocket)
        {
            throw new NotSupportedException("This provider currently uses the Responses server-sent-event transport.");
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        var credential = _getCredentialAsync is null
            ? new OpenAIRequestCredential(
                _getApiKeyAsync is null
                    ? _apiKey
                    : await _getApiKeyAsync(cancellationToken).ConfigureAwait(false))
            : await _getCredentialAsync(cancellationToken).ConfigureAwait(false)
              ?? throw new InvalidOperationException("The credential provider returned null.");
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
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadBoundedAsync(response.Content, _maxErrorCharacters, cancellationToken).ConfigureAwait(false);
            throw new ModelProviderException(
                $"The Responses endpoint returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {error}",
                IsTransient(response),
                GetRetryAfter(response),
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
            || options.MaxToolCallsPerResponse is < 1 or > 10_000)
        {
            throw new ArgumentException("One or more provider bounds or compatibility settings are invalid.", nameof(options));
        }

        if (options.Headers.Count > 64)
        {
            throw new ArgumentException("At most 64 custom headers may be configured.", nameof(options));
        }

        ValidateCredential(options.ApiKey, nameof(options));
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
        foreach (var header in options.Headers)
        {
            ValidateHeader(header.Key, header.Value, nameof(options));
        }
    }

    private void ApplyHeaders(
        HttpRequestMessage request,
        OpenAIRequestCredential credential,
        ModelRequest modelRequest)
    {
        if (credential.Headers.Count > 64)
        {
            throw new InvalidOperationException("A request credential cannot carry more than 64 headers.");
        }

        var apiKey = credential.ApiKey;
        ValidateCredential(apiKey, nameof(OpenAIResponsesProviderOptions.GetApiKeyAsync));
        foreach (var header in _headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                throw new InvalidOperationException($"Header '{header.Key}' is not valid for an HTTP request.");
            }
        }

        foreach (var header in credential.Headers)
        {
            ValidateHeader(header.Key, header.Value, nameof(OpenAIResponsesProviderOptions.GetCredentialAsync));
            request.Headers.Remove(header.Key);
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                throw new InvalidOperationException($"Credential header '{header.Key}' is not valid for an HTTP request.");
            }
        }

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
            request.Headers.TryAddWithoutValidation(header.Item1, header.Item2);
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

    private static bool IsTransient(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-should-retry", out var values))
        {
            var directive = values.FirstOrDefault();
            if (string.Equals(directive, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(directive, "false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var status = (int)response.StatusCode;
        return status is 408 or 409 or 429 || status >= 500;
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("retry-after-ms", out var values)
            && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds)
            && !double.IsNaN(milliseconds)
            && !double.IsInfinity(milliseconds))
        {
            return milliseconds >= TimeSpan.MaxValue.TotalMilliseconds
                ? TimeSpan.MaxValue
                : TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        }

        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        return null;
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
}
