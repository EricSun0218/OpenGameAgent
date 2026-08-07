using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Providers.OpenAICompatible;

public delegate ValueTask<string?> ApiKeyProvider(CancellationToken cancellationToken);

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

    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public int MaxEventCharacters { get; set; } = 4_000_000;

    public int MaxErrorCharacters { get; set; } = 64_000;

    public int MaxRequestBytes { get; set; } = 16_000_000;

    public int MaxResponseCharacters { get; set; } = 16_000_000;

    public int MaxToolCallsPerResponse { get; set; } = 256;

    public bool IncludeUsage { get; set; } = true;
}

public sealed class OpenAICompatibleProvider : IModelProvider
{
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly string? _apiKey;
    private readonly ApiKeyProvider? _getApiKey;
    private readonly string _apiKeyHeader;
    private readonly string _apiKeyScheme;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly int _maxEventCharacters;
    private readonly int _maxErrorCharacters;
    private readonly int _maxRequestBytes;
    private readonly int _maxResponseCharacters;
    private readonly int _maxToolCallsPerResponse;
    private readonly bool _includeUsage;

    public OpenAICompatibleProvider(OpenAICompatibleProviderOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (options.MaxEventCharacters < 1 || options.MaxEventCharacters > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxEventCharacters));
        }

        if (options.MaxErrorCharacters < 1 || options.MaxErrorCharacters > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxErrorCharacters));
        }

        if (options.MaxRequestBytes < 2 || options.MaxRequestBytes > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxRequestBytes));
        }

        if (options.MaxResponseCharacters < 1 || options.MaxResponseCharacters > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxResponseCharacters));
        }

        if (options.MaxToolCallsPerResponse < 1 || options.MaxToolCallsPerResponse > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxToolCallsPerResponse));
        }


        if (options.Endpoint is null
            || !options.Endpoint.IsAbsoluteUri
            || options.Endpoint.UserInfo.Length > 0
            || (!string.Equals(options.Endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(options.Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The model endpoint must be an absolute HTTP or HTTPS URI.", nameof(options.Endpoint));
        }

        ValidateHeader(options.ApiKeyHeader, string.Empty, nameof(options.ApiKeyHeader));
        ValidateCredential(options.ApiKey, nameof(options.ApiKey));
        ValidateCredential(options.ApiKeyScheme, nameof(options.ApiKeyScheme));
        foreach (var header in options.Headers)
        {
            ValidateHeader(header.Key, header.Value, nameof(options.Headers));
        }

        if ((!string.IsNullOrEmpty(options.ApiKey) || options.GetApiKeyAsync is not null)
            && options.Headers.ContainsKey(options.ApiKeyHeader))
        {
            throw new ArgumentException("Custom headers cannot also define the configured API key header.", nameof(options.Headers));
        }

        _httpClient = options.HttpClient;
        _endpoint = options.Endpoint;
        _apiKey = options.ApiKey;
        _getApiKey = options.GetApiKeyAsync;
        _apiKeyHeader = string.IsNullOrWhiteSpace(options.ApiKeyHeader)
            ? throw new ArgumentException("An API key header is required.", nameof(options.ApiKeyHeader))
            : options.ApiKeyHeader;
        _apiKeyScheme = options.ApiKeyScheme ?? string.Empty;
        _headers = new Dictionary<string, string>(options.Headers, StringComparer.OrdinalIgnoreCase);
        _maxEventCharacters = options.MaxEventCharacters;
        _maxErrorCharacters = options.MaxErrorCharacters;
        _maxRequestBytes = options.MaxRequestBytes;
        _maxResponseCharacters = options.MaxResponseCharacters;
        _maxToolCallsPerResponse = options.MaxToolCallsPerResponse;
        _includeUsage = options.IncludeUsage;
    }

    private static void ValidateHeader(string name, string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(name)
            || value is null
            || value.Contains('\r')
            || value.Contains('\n'))
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

        if ((value?.Contains('\r') ?? false) || (value?.Contains('\n') ?? false))
        {
            throw new ArgumentException("Credentials cannot contain line breaks.", parameterName);
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

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        var apiKey = _getApiKey is null
            ? _apiKey
            : await _getApiKey(cancellationToken).ConfigureAwait(false);
        ValidateCredential(apiKey, nameof(OpenAICompatibleProviderOptions.GetApiKeyAsync));
        ApplyHeaders(httpRequest, apiKey);
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
            throw new HttpRequestException(
                $"The model endpoint returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {error}");
        }

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var cancellationRegistration = cancellationToken.Register(stream.Dispose);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
        var state = new StreamState(request.Model, _maxResponseCharacters, _maxToolCallsPerResponse);
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

        if (!sawDone && !state.HasFinishReason)
        {
            throw new InvalidDataException("The model stream ended before a terminal marker or finish reason was received.");
        }

        yield return ModelStreamEvent.Terminal(state.Complete());
    }

    private void ApplyHeaders(HttpRequestMessage request, string? apiKey)
    {
        foreach (var header in _headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                throw new InvalidOperationException($"Header '{header.Key}' is not valid for an HTTP request.");
            }
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            return;
        }

        var value = string.IsNullOrWhiteSpace(_apiKeyScheme) ? apiKey : _apiKeyScheme + " " + apiKey;
        if (!request.Headers.TryAddWithoutValidation(_apiKeyHeader, value))
        {
            throw new InvalidOperationException($"API key header '{_apiKeyHeader}' is not valid for an HTTP request.");
        }
    }

    private byte[] SerializeRequest(ModelRequest request)
    {
        EnsureRequestCanFit(request);
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Model,
            ["messages"] = ProjectMessages(request),
            ["stream"] = true,
        };
        if (_includeUsage)
        {
            payload["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true };
        }

        if (request.Tools.Count > 0)
        {
            payload["tools"] = request.Tools.Select(tool => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = ParseElement(tool.InputSchemaJson),
                },
            }).ToArray();
            payload["tool_choice"] = "auto";
        }

        if (request.Parameters.Temperature is { } temperature)
        {
            payload["temperature"] = temperature;
        }

        if (request.Parameters.MaxOutputTokens is { } maximum)
        {
            payload["max_tokens"] = maximum;
        }

        if (!string.IsNullOrWhiteSpace(request.Parameters.ReasoningLevel))
        {
            payload["reasoning_effort"] = request.Parameters.ReasoningLevel;
        }

        foreach (var extension in request.Parameters.Extensions ?? new Dictionary<string, string>())
        {
            if (payload.ContainsKey(extension.Key))
            {
                throw new InvalidOperationException($"Model extension '{extension.Key}' cannot override a core request field.");
            }

            payload[extension.Key] = ParseExtension(extension.Value);
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (body.Length > _maxRequestBytes)
        {
            throw new InvalidDataException("The model request exceeded the configured byte limit.");
        }

        return body;
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

    private static IReadOnlyList<object> ProjectMessages(ModelRequest request)
    {
        var projected = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["role"] = "system",
                ["content"] = request.SystemPrompt,
            },
        };
        foreach (var message in request.Messages)
        {
            projected.Add(ProjectMessage(message));
        }

        return projected;
    }

    private static object ProjectMessage(AgentMessage message)
    {
        if (message.Role == AgentRole.Assistant)
        {
            var assistant = new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = JoinContent(message.Content.Where(content => content is not ToolCallContent)),
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

            return assistant;
        }

        if (message.Role == AgentRole.Tool)
        {
            return new Dictionary<string, object?>
            {
                ["role"] = "tool",
                ["tool_call_id"] = message.ToolCallId,
                ["content"] = JoinContent(message.Content),
            };
        }

        const string role = "user";
        var content = JoinContent(message.Content);
        if (message.Role == AgentRole.Custom)
        {
            content = "[" + message.CustomRole + "]\n" + content;
        }

        return new Dictionary<string, object?>
        {
            ["role"] = role,
            ["content"] = content,
        };
    }

    private static string JoinContent(IEnumerable<AgentContent> content)
    {
        return string.Join("\n", content.Where(part => part is not ReasoningContent).Select(part => part switch
        {
            TextContent text => text.Text,
            JsonContent json => json.Json,
            ResourceContent resource => $"[resource name={resource.Name ?? "unnamed"} media_type={resource.MediaType}] {resource.Uri}",
            ToolCallContent call => $"[tool_call {call.Name}] {call.ArgumentsJson}",
            _ => string.Empty,
        }));
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
        private ModelStopReason _stopReason = ModelStopReason.Stop;
        private ModelUsage _usage = new();
        private string? _errorMessage;
        private bool _textStarted;
        private bool _reasoningStarted;
        private bool _contentEnded;
        private bool _hasFinishReason;
        private readonly int _maximumCharacters;
        private readonly int _maximumToolCalls;
        private long _characters;

        public StreamState(string model, int maximumCharacters, int maximumToolCalls)
        {
            _ = model;
            _maximumCharacters = maximumCharacters;
            _maximumToolCalls = maximumToolCalls;
        }

        public IReadOnlyList<ModelStreamEvent> Apply(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
                var root = document.RootElement;
                RequireKind(root, JsonValueKind.Object, "A model stream event must be a JSON object.");
                EnsureUnambiguous(root, "The model stream contains duplicate JSON property names.");
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
                    ApplyText(delta, "reasoning_content", _reasoning, ref _reasoningStarted, ModelStreamEventKind.ReasoningStarted, ModelStreamEventKind.ReasoningDelta, updates);
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
                var index = _tools.Count;
                if (call.TryGetProperty("index", out var indexElement)
                    && (!indexElement.TryGetInt32(out index)))
                {
                    throw new InvalidDataException("A model tool call index must be an integer.");
                }

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
                    created = true;
                }

                if (call.TryGetProperty("id", out var id))
                {
                    RequireKind(id, JsonValueKind.String, "A model tool call ID must be a string.");
                    var idText = id.GetString()!;
                    if (builder.Id is not null && !string.Equals(builder.Id, idText, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("A streamed model tool call changed its ID.");
                    }

                    AddCharacters(idText.Length);
                    builder.Id = idText;
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
                            contentIndex: index,
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
                            index,
                            builder.Id,
                            builder.Name.Length == 0 ? null : builder.Name.ToString()));
                    }
                }
                else if (created)
                {
                    updates.Add(ModelStreamEvent.Update(
                        ModelStreamEventKind.ToolCallStarted,
                        Partial(),
                        contentIndex: index,
                        toolCallId: builder.Id));
                }
            }
        }

        private void AddEndedEvents(ICollection<ModelStreamEvent> updates)
        {
            if (_reasoningStarted)
            {
                updates.Add(ModelStreamEvent.Update(ModelStreamEventKind.ReasoningEnded, Partial()));
            }

            if (_textStarted)
            {
                updates.Add(ModelStreamEvent.Update(ModelStreamEventKind.TextEnded, Partial()));
            }

            foreach (var pair in _tools)
            {
                var tool = pair.Value;
                updates.Add(ModelStreamEvent.Update(
                    ModelStreamEventKind.ToolCallEnded,
                    Partial(),
                    contentIndex: pair.Key,
                    toolCallId: tool.Id,
                    toolName: tool.Name.Length == 0 ? null : tool.Name.ToString()));
            }
        }

        public ModelResponse Partial() => new(CurrentContent(includeTools: false), ModelStopReason.Pending, _usage);

        public bool HasFinishReason => _hasFinishReason;

        public ModelResponse Complete()
        {
            if (_stopReason == ModelStopReason.ToolUse
                && _tools.Any(pair => string.IsNullOrWhiteSpace(pair.Value.Id)
                                      || pair.Value.Name.Length == 0))
            {
                throw new InvalidDataException("A completed model tool call is missing its ID or function name.");
            }

            var content = CurrentContent(includeTools: true);
            return new ModelResponse(content, _stopReason, _usage, _errorMessage);
        }

        private IReadOnlyList<AgentContent> CurrentContent(bool includeTools)
        {
            var content = new List<AgentContent>();
            if (_reasoning.Length > 0)
            {
                content.Add(new ReasoningContent(_reasoning.ToString()));
            }

            if (_text.Length > 0)
            {
                content.Add(new TextContent(_text.ToString()));
            }

            if (includeTools)
            {
                foreach (var pair in _tools)
                {
                    var tool = pair.Value;
                    var arguments = tool.Arguments.Length == 0 ? "{}" : tool.Arguments.ToString();
                    if (_stopReason == ModelStopReason.Length && !IsJsonObject(arguments))
                    {
                        arguments = "{}";
                    }

                    content.Add(new ToolCallContent(
                        string.IsNullOrWhiteSpace(tool.Id) ? "call_" + pair.Key : tool.Id,
                        tool.Name.Length == 0 ? "unknown_tool" : tool.Name.ToString(),
                        arguments));
                }
            }

            return content;
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
                updates.Add(ModelStreamEvent.Update(startedKind, Partial()));
            }

            AddCharacters(text.Length);
            builder.Append(text);
            updates.Add(ModelStreamEvent.Update(deltaKind, Partial(), text));
        }

        private void ReadFinishReason(JsonElement choice)
        {
            if (!choice.TryGetProperty("finish_reason", out var reason) || reason.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            RequireKind(reason, JsonValueKind.String, "A model finish reason must be a string or null.");
            _hasFinishReason = true;
            _stopReason = reason.GetString() switch
            {
                "tool_calls" or "function_call" => ModelStopReason.ToolUse,
                "length" => ModelStopReason.Length,
                "stop" => ModelStopReason.Stop,
                var unknown => SetError("The model stopped with unsupported finish reason '" + unknown + "'."),
            };
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
            if (usage.TryGetProperty("prompt_tokens_details", out var details))
            {
                if (details.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Model usage token details must be an object.");
                }

                cached = ReadNonNegativeLong(details, "cached_tokens");
            }

            if (cached > prompt)
            {
                throw new InvalidDataException("Cached prompt tokens cannot exceed total prompt tokens.");
            }

            _usage = new ModelUsage(prompt - cached, output, cached);
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
    }
}
