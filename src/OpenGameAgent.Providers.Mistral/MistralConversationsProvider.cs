using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Providers.Mistral;

public delegate ValueTask<string?> MistralApiKeyProvider(CancellationToken cancellationToken);

public enum MistralToolChoice
{
    Auto,
    None,
    Any,
    Required,
    Function,
}

public enum MistralReasoningMode
{
    Auto,
    PromptMode,
    Effort,
}

public sealed class MistralConversationsProviderOptions
{
    public MistralConversationsProviderOptions(HttpClient httpClient, Uri endpoint)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public HttpClient HttpClient { get; }

    public Uri Endpoint { get; }

    public string? ApiKey { get; set; }

    public MistralApiKeyProvider? GetApiKeyAsync { get; set; }

    public IDictionary<string, string> Headers { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string ProviderId { get; set; } = "mistral";

    public string ApiId { get; set; } = "mistral-conversations";

    public bool SupportsImages { get; set; } = true;

    public MistralToolChoice? ToolChoice { get; set; }

    public string? RequiredToolName { get; set; }

    public MistralReasoningMode ReasoningMode { get; set; } = MistralReasoningMode.Auto;

    public bool AllowInsecureHttp { get; set; }

    public int MaxEventCharacters { get; set; } = 4_000_000;

    public int MaxErrorCharacters { get; set; } = 4_000;

    public int MaxRequestBytes { get; set; } = 16_000_000;

    public int MaxResponseCharacters { get; set; } = 16_000_000;

    public int MaxToolCallsPerResponse { get; set; } = 256;
}

public sealed class MistralConversationsProvider : IModelProvider, IModelProviderCapabilities
{
    private readonly MistralConversationsProviderOptions _options;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly IReadOnlyCollection<string> _supportedApis;

    public MistralConversationsProvider(MistralConversationsProviderOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
        _headers = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(options.Headers, StringComparer.OrdinalIgnoreCase));
        _supportedApis = Array.AsReadOnly(new[] { options.ApiId });
    }

    public IReadOnlyCollection<string> SupportedApis => _supportedApis;

    public bool SupportsNativeDeferredTools => false;

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
            throw new NotSupportedException("This provider currently uses the Mistral server-sent-event transport.");
        }

        var apiKey = _options.GetApiKeyAsync is null
            ? _options.ApiKey
            : await _options.GetApiKeyAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("A Mistral API key is required.");
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        ApplyHeaders(httpRequest, apiKey!, request);
        httpRequest.Content = new ByteArrayContent(SerializeRequest(request));
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var response = await _options.HttpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadBoundedAsync(response.Content, _options.MaxErrorCharacters, cancellationToken)
                .ConfigureAwait(false);
            throw new ModelProviderException(
                $"The Mistral endpoint returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {error}",
                IsTransient(response),
                GetRetryAfter(response),
                (int)response.StatusCode);
        }

        using var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var registration = cancellationToken.Register(responseStream.Dispose);
        using var reader = new StreamReader(responseStream, Encoding.UTF8, true, 4096, leaveOpen: false);
        var state = new MistralStreamState(
            request.Model,
            _options.ProviderId,
            _options.ApiId,
            _options.MaxResponseCharacters,
            _options.MaxToolCallsPerResponse);
        yield return ModelStreamEvent.Update(ModelStreamEventKind.Started, state.Partial());
        await foreach (var data in ReadSseDataAsync(reader, _options.MaxEventCharacters, cancellationToken))
        {
            if (data.Length == 0 || string.Equals(data, "[DONE]", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var update in state.Apply(data))
            {
                yield return update;
            }
        }

        foreach (var update in state.CloseOpenBlocks())
        {
            yield return update;
        }

        yield return ModelStreamEvent.Terminal(state.Complete());
    }

    private byte[] SerializeRequest(ModelRequest request)
    {
        var normalizer = MistralToolCallIds.CreateNormalizer();
        var messages = ProviderTranscript.Normalize(
            request.Messages,
            _options.ProviderId,
            _options.ApiId,
            request.Model,
            normalizer);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["stream"] = true,
            ["messages"] = ProjectMessages(messages, request.SystemPrompt),
        };
        if (request.Tools.Count > 0)
        {
            payload["tools"] = ProjectTools(request.Tools);
        }

        if (request.Parameters.Temperature is { } temperature)
        {
            payload["temperature"] = temperature;
        }

        if (request.Parameters.MaxOutputTokens is { } maxTokens)
        {
            payload["max_tokens"] = maxTokens;
        }

        ApplyToolChoice(payload);
        ApplyReasoning(payload, request.Model, request.Parameters.ReasoningLevel);
        if (request.Parameters.CacheRetention != ModelCacheRetention.None
            && !string.IsNullOrWhiteSpace(request.SessionId))
        {
            payload["prompt_cache_key"] = request.SessionId;
        }

        MergeSampling(payload, request.Parameters.SamplingParametersJson);
        foreach (var extension in request.Parameters.Extensions)
        {
            if (payload.ContainsKey(extension.Key))
            {
                throw new InvalidOperationException($"Model extension '{extension.Key}' cannot override a core request field.");
            }

            payload[extension.Key] = ParseJsonOrString(extension.Value);
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (bytes.Length > _options.MaxRequestBytes)
        {
            throw new InvalidOperationException("The Mistral request exceeded the configured byte limit.");
        }

        return bytes;
    }

    private IReadOnlyList<object> ProjectMessages(IReadOnlyList<AgentMessage> messages, string systemPrompt)
    {
        var result = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            result.Add(new Dictionary<string, object?>
            {
                ["role"] = "system",
                ["content"] = SanitizeUnicode(systemPrompt),
            });
        }

        foreach (var message in messages)
        {
            if (message.Role is AgentRole.User or AgentRole.Custom)
            {
                var content = ProjectUserContent(message.Content);
                if (content.Count > 0)
                {
                    result.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = content });
                }

                continue;
            }

            if (message.Role == AgentRole.Assistant)
            {
                var content = new List<object>();
                var calls = new List<object>();
                foreach (var item in message.Content)
                {
                    if (item is TextContent text && !string.IsNullOrWhiteSpace(text.Text))
                    {
                        content.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = SanitizeUnicode(text.Text) });
                    }
                    else if (item is ReasoningContent reasoning && !string.IsNullOrWhiteSpace(reasoning.Text))
                    {
                        content.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "thinking",
                            ["thinking"] = new object[]
                            {
                                new Dictionary<string, object?> { ["type"] = "text", ["text"] = SanitizeUnicode(reasoning.Text) },
                            },
                        });
                    }
                    else if (item is ToolCallContent call)
                    {
                        calls.Add(new Dictionary<string, object?>
                        {
                            ["id"] = call.Id,
                            ["type"] = "function",
                            ["function"] = new Dictionary<string, object?>
                            {
                                ["name"] = call.Name,
                                ["arguments"] = call.ArgumentsJson,
                            },
                        });
                    }
                }

                if (content.Count > 0 || calls.Count > 0)
                {
                    var projected = new Dictionary<string, object?> { ["role"] = "assistant" };
                    if (content.Count > 0)
                    {
                        projected["content"] = content;
                    }

                    if (calls.Count > 0)
                    {
                        projected["tool_calls"] = calls;
                    }

                    result.Add(projected);
                }

                continue;
            }

            if (message.Role == AgentRole.Tool)
            {
                result.Add(new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = message.ToolCallId,
                    ["name"] = message.ToolName,
                    ["content"] = ProjectToolResult(message),
                });
            }
        }

        return result;
    }

    private IReadOnlyList<object> ProjectUserContent(IEnumerable<AgentContent> content)
    {
        var result = new List<object>();
        foreach (var item in content)
        {
            switch (item)
            {
                case TextContent text:
                    result.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = SanitizeUnicode(text.Text) });
                    break;
                case JsonContent json:
                    result.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = json.Json });
                    break;
                case BinaryContent binary when binary.MediaKind == AgentMediaKind.Image && _options.SupportsImages:
                    result.Add(ImageContent(binary));
                    break;
                case BinaryContent binary:
                    result.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = $"(binary omitted: {binary.MediaType})" });
                    break;
                case ResourceContent resource:
                    result.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = $"[resource media_type={resource.MediaType}] {resource.Uri}",
                    });
                    break;
            }
        }

        return result;
    }

    private IReadOnlyList<object> ProjectToolResult(AgentMessage message)
    {
        var result = new List<object>();
        var text = string.Join("\n", message.Content.Select(item => item switch
        {
            TextContent value => value.Text,
            JsonContent value => value.Json,
            _ => null,
        }).Where(value => value is not null)).Trim();
        var hasImages = message.Content.OfType<BinaryContent>().Any(value => value.MediaKind == AgentMediaKind.Image);
        var value = BuildToolResultText(text, hasImages, _options.SupportsImages, message.IsError);
        result.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = value });
        if (_options.SupportsImages)
        {
            result.AddRange(message.Content.OfType<BinaryContent>()
                .Where(binary => binary.MediaKind == AgentMediaKind.Image)
                .Select(binary => (object)ImageContent(binary)));
        }

        return result;
    }

    private static IReadOnlyList<object> ProjectTools(IEnumerable<ToolDefinition> tools)
    {
        return tools.Select(tool =>
        {
            if (tool.ConstrainedSampling?.Kind == ToolConstrainedSamplingKind.Grammar)
            {
                throw new NotSupportedException("Mistral function tools do not support grammar-constrained sampling.");
            }

            using var schema = JsonDocument.Parse(tool.InputSchemaJson);
            return (object)new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = schema.RootElement.Clone(),
                    ["strict"] = tool.ConstrainedSampling?.Kind == ToolConstrainedSamplingKind.JsonSchema,
                },
            };
        }).ToArray();
    }

    private void ApplyToolChoice(IDictionary<string, object?> payload)
    {
        if (_options.ToolChoice is null)
        {
            return;
        }

        payload["tool_choice"] = _options.ToolChoice switch
        {
            MistralToolChoice.Auto => "auto",
            MistralToolChoice.None => "none",
            MistralToolChoice.Any => "any",
            MistralToolChoice.Required => "required",
            MistralToolChoice.Function => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?> { ["name"] = _options.RequiredToolName },
            },
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private void ApplyReasoning(IDictionary<string, object?> payload, string model, string? level)
    {
        if (string.IsNullOrWhiteSpace(level) || string.Equals(level, "off", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var useEffort = _options.ReasoningMode == MistralReasoningMode.Effort
                        || _options.ReasoningMode == MistralReasoningMode.Auto && UsesReasoningEffort(model);
        if (useEffort)
        {
            payload["reasoning_effort"] = "high";
        }
        else
        {
            payload["prompt_mode"] = "reasoning";
        }
    }

    private static bool UsesReasoningEffort(string model)
    {
        var lower = model.ToLowerInvariant();
        return lower is "mistral-small-2603" or "mistral-small-latest" or "mistral-medium-3.5" or "mistral-medium-3-5";
    }

    private void ApplyHeaders(HttpRequestMessage request, string apiKey, ModelRequest modelRequest)
    {
        foreach (var header in _headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                throw new InvalidOperationException($"Mistral request header '{header.Key}' is invalid.");
            }
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (modelRequest.Parameters.CacheRetention != ModelCacheRetention.None
            && !string.IsNullOrWhiteSpace(modelRequest.SessionId)
            && !request.Headers.Contains("x-affinity"))
        {
            request.Headers.TryAddWithoutValidation("x-affinity", modelRequest.SessionId);
        }
    }

    private static Dictionary<string, object?> ImageContent(BinaryContent binary) => new()
    {
        ["type"] = "image_url",
        ["image_url"] = $"data:{binary.MediaType};base64,{binary.Data}",
    };

    private static string BuildToolResultText(string text, bool hasImages, bool supportsImages, bool isError)
    {
        var prefix = isError ? "[tool error] " : string.Empty;
        if (text.Length > 0)
        {
            return prefix + text + (hasImages && !supportsImages ? "\n[tool image omitted: model does not support images]" : string.Empty);
        }

        if (hasImages)
        {
            return prefix + (supportsImages ? "(see attached image)" : "(image omitted: model does not support images)");
        }

        return prefix + "(no tool output)";
    }

    private static void MergeSampling(IDictionary<string, object?> payload, string? json)
    {
        if (json is null)
        {
            return;
        }

        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (payload.ContainsKey(property.Name))
            {
                throw new InvalidOperationException($"Sampling parameter '{property.Name}' cannot override a core request field.");
            }

            payload[property.Name] = property.Value.Clone();
        }
    }

    private static object ParseJsonOrString(string value)
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

    private static string SanitizeUnicode(string value)
    {
        StringBuilder? builder = null;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                if (builder is not null)
                {
                    builder.Append(character);
                    builder.Append(value[++index]);
                }
                else
                {
                    index++;
                }

                continue;
            }

            if (!char.IsSurrogate(character))
            {
                builder?.Append(character);
                continue;
            }

            builder ??= new StringBuilder(value.Substring(0, index));
            builder.Append('\uFFFD');
        }

        return builder?.ToString() ?? value;
    }

    private static bool IsTransient(HttpResponseMessage response)
    {
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
            return TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        }

        return response.Headers.RetryAfter?.Delta;
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, int maximumCharacters, CancellationToken cancellationToken)
    {
        using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var registration = cancellationToken.Register(stream.Dispose);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
        var buffer = new char[Math.Min(4096, maximumCharacters)];
        var builder = new StringBuilder();
        while (builder.Length < maximumCharacters)
        {
            var read = await reader.ReadAsync(buffer, 0, Math.Min(buffer.Length, maximumCharacters - builder.Length)).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }

    private static async IAsyncEnumerable<string> ReadSseDataAsync(
        StreamReader reader,
        int maximumCharacters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var data = new StringBuilder();
        await foreach (var line in ReadBoundedLinesAsync(reader, maximumCharacters, cancellationToken))
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return data.ToString();
                }

                data.Clear();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line.Substring(5).TrimStart());
                if (data.Length > maximumCharacters)
                {
                    throw new InvalidDataException("A Mistral SSE event exceeded the configured size limit.");
                }
            }
        }

        if (data.Length > 0)
        {
            yield return data.ToString();
        }
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
                var read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
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
                            throw new InvalidDataException("A Mistral SSE line exceeded the configured size limit.");
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

    private static void ValidateOptions(MistralConversationsProviderOptions options)
    {
        if (!options.AllowInsecureHttp && options.Endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The Mistral endpoint must use HTTPS.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.ProviderId) || string.IsNullOrWhiteSpace(options.ApiId))
        {
            throw new ArgumentException("Mistral provider and API identifiers are required.", nameof(options));
        }

        if (!Enum.IsDefined(typeof(MistralReasoningMode), options.ReasoningMode)
            || options.ToolChoice is { } choice && !Enum.IsDefined(typeof(MistralToolChoice), choice))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.ToolChoice == MistralToolChoice.Function && string.IsNullOrWhiteSpace(options.RequiredToolName))
        {
            throw new ArgumentException("A required Mistral function name is missing.", nameof(options));
        }

        if (options.ToolChoice != MistralToolChoice.Function && options.RequiredToolName is not null)
        {
            throw new ArgumentException("Only function tool choice can carry a required function name.", nameof(options));
        }

        if (options.MaxEventCharacters <= 0
            || options.MaxErrorCharacters <= 0
            || options.MaxRequestBytes <= 0
            || options.MaxResponseCharacters <= 0
            || options.MaxToolCallsPerResponse <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Mistral protocol limits must be positive.");
        }
    }
}
