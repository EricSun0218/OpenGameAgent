using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Providers.Anthropic;

public delegate ValueTask<string?> AnthropicApiKeyProvider(CancellationToken cancellationToken);

public enum AnthropicThinkingDisplay
{
    Summarized,
    Omitted,
}

public sealed class AnthropicMessagesProviderOptions
{
    public AnthropicMessagesProviderOptions(HttpClient httpClient, Uri endpoint)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public HttpClient HttpClient { get; }

    public Uri Endpoint { get; }

    public string? ApiKey { get; set; }

    public AnthropicApiKeyProvider? GetApiKeyAsync { get; set; }

    public IDictionary<string, string> Headers { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string ProviderId { get; set; } = "anthropic";

    public string ApiId { get; set; } = "anthropic-messages";

    public string ApiVersion { get; set; } = "2023-06-01";

    public bool AllowInsecureHttp { get; set; }

    public bool SupportsEagerToolInputStreaming { get; set; } = true;

    public bool SupportsLongCacheRetention { get; set; } = true;

    public bool SendSessionAffinityHeaders { get; set; }

    public bool SupportsCacheControlOnTools { get; set; } = true;

    public bool SupportsTemperature { get; set; } = true;

    public bool ForceAdaptiveThinking { get; set; }

    public bool AllowEmptyThinkingSignature { get; set; }

    public bool SupportsStrictTools { get; set; }

    public bool SupportsToolReferences { get; set; }

    public bool InterleavedThinking { get; set; } = true;

    public AnthropicThinkingDisplay ThinkingDisplay { get; set; } = AnthropicThinkingDisplay.Summarized;

    public int DefaultMaxOutputTokens { get; set; } = 4096;

    public int MaxEventCharacters { get; set; } = 4_000_000;

    public int MaxErrorCharacters { get; set; } = 64_000;

    public int MaxRequestBytes { get; set; } = 16_000_000;

    public int MaxResponseCharacters { get; set; } = 16_000_000;

    public int MaxToolCallsPerResponse { get; set; } = 256;
}

public sealed class AnthropicMessagesProvider : IModelProvider, IModelProviderCapabilities
{
    private const string FineGrainedToolStreamingBeta = "fine-grained-tool-streaming-2025-05-14";
    private const string InterleavedThinkingBeta = "interleaved-thinking-2025-05-14";
    private readonly AnthropicMessagesProviderOptions _options;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly IReadOnlyCollection<string> _supportedApis;

    public AnthropicMessagesProvider(AnthropicMessagesProviderOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
        _headers = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(options.Headers, StringComparer.OrdinalIgnoreCase));
        _supportedApis = Array.AsReadOnly(new[] { options.ApiId });
    }

    public IReadOnlyCollection<string> SupportedApis => _supportedApis;

    public bool SupportsNativeDeferredTools => _options.SupportsToolReferences;

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
            throw new NotSupportedException("Anthropic Messages uses a server-sent-event transport.");
        }

        var apiKey = _options.GetApiKeyAsync is null
            ? _options.ApiKey
            : await _options.GetApiKeyAsync(cancellationToken).ConfigureAwait(false);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        ApplyHeaders(httpRequest, apiKey, request);
        httpRequest.Content = new ByteArrayContent(SerializeRequest(request));
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };

        using var response = await _options.HttpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadBoundedAsync(response.Content, _options.MaxErrorCharacters, cancellationToken)
                .ConfigureAwait(false);
            throw new ModelProviderException(
                $"The Anthropic endpoint returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {error}",
                IsTransient(response),
                GetRetryAfter(response),
                (int)response.StatusCode);
        }

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var cancellationRegistration = cancellationToken.Register(stream.Dispose);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
        var state = new AnthropicStreamState(
            request.Model,
            _options.ProviderId,
            _options.ApiId,
            _options.MaxResponseCharacters,
            _options.MaxToolCallsPerResponse);
        yield return ModelStreamEvent.Update(ModelStreamEventKind.Started, state.Partial());

        await foreach (var serverEvent in ReadSseEventsAsync(reader, _options.MaxEventCharacters, cancellationToken))
        {
            if (serverEvent.Data.Length == 0)
            {
                continue;
            }

            foreach (var update in state.Apply(serverEvent.Name, serverEvent.Data))
            {
                yield return update;
            }
        }

        yield return ModelStreamEvent.Terminal(state.Complete());
    }

    private byte[] SerializeRequest(ModelRequest request)
    {
        var normalizedMessages = ProviderTranscript.Normalize(
            request.Messages,
            _options.ProviderId,
            _options.ApiId,
            request.Model,
            (id, _, _, _) => NormalizeToolCallId(id));
        var placement = SplitTools(request.Tools, normalizedMessages);
        var cacheControl = CacheControl(request.Parameters.CacheRetention);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = ProjectMessages(normalizedMessages, placement.Deferred, cacheControl),
            ["max_tokens"] = request.Parameters.MaxOutputTokens ?? _options.DefaultMaxOutputTokens,
            ["stream"] = true,
        };

        if (request.SystemPrompt.Length > 0)
        {
            var system = new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = request.SystemPrompt,
            };
            if (cacheControl is not null)
            {
                system["cache_control"] = cacheControl;
            }

            payload["system"] = new[] { system };
        }

        if (placement.Immediate.Count > 0 || placement.Deferred.Count > 0)
        {
            var tools = new List<object>();
            tools.AddRange(ProjectTools(placement.Immediate, cacheControl, deferLoading: false));
            tools.AddRange(ProjectTools(placement.Deferred.Values, null, deferLoading: true));
            payload["tools"] = tools;
        }

        ApplyThinking(payload, request.Parameters);
        if (request.Parameters.Temperature is { } temperature
            && string.IsNullOrWhiteSpace(request.Parameters.ReasoningLevel)
            && _options.SupportsTemperature)
        {
            payload["temperature"] = temperature;
        }

        if (request.Parameters.MetadataJson is { } metadataJson)
        {
            using var metadata = JsonDocument.Parse(metadataJson);
            if (metadata.RootElement.TryGetProperty("user_id", out var userId)
                && userId.ValueKind == JsonValueKind.String)
            {
                payload["metadata"] = new Dictionary<string, object?> { ["user_id"] = userId.GetString() };
            }
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
        if (body.Length > _options.MaxRequestBytes)
        {
            throw new InvalidDataException("The Anthropic request exceeded the configured byte limit.");
        }

        return body;
    }

    private void ApplyThinking(IDictionary<string, object?> payload, ModelParameters parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.ReasoningLevel))
        {
            return;
        }

        var display = _options.ThinkingDisplay == AnthropicThinkingDisplay.Omitted ? "omitted" : "summarized";
        if (_options.ForceAdaptiveThinking)
        {
            payload["thinking"] = new Dictionary<string, object?>
            {
                ["type"] = "adaptive",
                ["display"] = display,
            };
            payload["output_config"] = new Dictionary<string, object?>
            {
                ["effort"] = parameters.ReasoningLevel,
            };
            return;
        }

        var budget = parameters.ReasoningBudgets.TryGetValue(parameters.ReasoningLevel!, out var configured)
            ? configured
            : 1024;
        payload["thinking"] = new Dictionary<string, object?>
        {
            ["type"] = "enabled",
            ["budget_tokens"] = budget,
            ["display"] = display,
        };
    }

    private IReadOnlyList<object> ProjectMessages(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyDictionary<string, ToolDefinition> deferredTools,
        object? cacheControl)
    {
        var projected = new List<MessageProjection>();
        var loadedTools = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role is AgentRole.User or AgentRole.Custom)
            {
                AddMessage(projected, "user", ProjectUserBlocks(message));
                continue;
            }

            if (message.Role == AgentRole.Assistant)
            {
                var blocks = new List<object>();
                foreach (var content in message.Content)
                {
                    switch (content)
                    {
                        case TextContent text when text.Text.Length > 0:
                            blocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = text.Text });
                            break;
                        case ReasoningContent reasoning when reasoning.Redacted:
                            blocks.Add(new Dictionary<string, object?>
                            {
                                ["type"] = "redacted_thinking",
                                ["data"] = reasoning.Signature,
                            });
                            break;
                        case ReasoningContent reasoning when !string.IsNullOrWhiteSpace(reasoning.Signature):
                            blocks.Add(new Dictionary<string, object?>
                            {
                                ["type"] = "thinking",
                                ["thinking"] = reasoning.Text,
                                ["signature"] = reasoning.Signature,
                            });
                            break;
                        case ReasoningContent reasoning when _options.AllowEmptyThinkingSignature:
                            blocks.Add(new Dictionary<string, object?>
                            {
                                ["type"] = "thinking",
                                ["thinking"] = reasoning.Text,
                                ["signature"] = string.Empty,
                            });
                            break;
                        case ReasoningContent reasoning when reasoning.Text.Length > 0:
                            blocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = reasoning.Text });
                            break;
                        case ToolCallContent call:
                            blocks.Add(new Dictionary<string, object?>
                            {
                                ["type"] = "tool_use",
                                ["id"] = call.Id,
                                ["name"] = call.Name,
                                ["input"] = ParseRequiredObject(call.ArgumentsJson),
                            });
                            break;
                    }
                }

                AddMessage(projected, "assistant", blocks);
                continue;
            }

            if (message.Role == AgentRole.Tool)
            {
                var results = new List<object>();
                var sibling = new List<object>();
                while (index < messages.Count && messages[index].Role == AgentRole.Tool)
                {
                    var toolMessage = messages[index];
                    var references = toolMessage.AddedToolNames
                        .Where(name => deferredTools.ContainsKey(name) && loadedTools.Add(name))
                        .Select(name => (object)new Dictionary<string, object?>
                        {
                            ["type"] = "tool_reference",
                            ["tool_name"] = name,
                        })
                        .ToArray();
                    var ordinary = ProjectToolResultContent(toolMessage.Content);
                    var result = new Dictionary<string, object?>
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = toolMessage.ToolCallId,
                        ["content"] = references.Length > 0 ? references : ordinary,
                        ["is_error"] = toolMessage.IsError,
                    };
                    results.Add(result);
                    if (references.Length > 0)
                    {
                        if (ordinary is string text)
                        {
                            sibling.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = text });
                        }
                        else if (ordinary is IEnumerable<object> content)
                        {
                            sibling.AddRange(content);
                        }
                    }

                    index++;
                }

                index--;
                results.AddRange(sibling);
                AddMessage(projected, "user", results);
            }
        }

        if (cacheControl is not null && projected.Count > 0)
        {
            var lastUser = projected.LastOrDefault(message => message.Role == "user");
            if (lastUser?.Content.LastOrDefault() is Dictionary<string, object?> lastBlock)
            {
                lastBlock["cache_control"] = cacheControl;
            }
        }

        return projected.Select(message => (object)new Dictionary<string, object?>
        {
            ["role"] = message.Role,
            ["content"] = message.Content,
        }).ToArray();
    }

    private static void AddMessage(List<MessageProjection> messages, string role, IEnumerable<object> content)
    {
        var blocks = content.ToArray();
        if (blocks.Length == 0)
        {
            return;
        }

        if (messages.Count > 0 && messages[^1].Role == role)
        {
            messages[^1].Content.AddRange(blocks);
        }
        else
        {
            messages.Add(new MessageProjection(role, blocks));
        }
    }

    private static IReadOnlyList<object> ProjectUserBlocks(AgentMessage message)
    {
        var blocks = new List<object>();
        if (message.Role == AgentRole.Custom)
        {
            blocks.Add(new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = "[" + message.CustomRole + "]",
            });
        }

        foreach (var content in message.Content)
        {
            switch (content)
            {
                case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                    blocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = text.Text });
                    break;
                case JsonContent json:
                    blocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = json.Json });
                    break;
                case BinaryContent binary when binary.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "image",
                        ["source"] = new Dictionary<string, object?>
                        {
                            ["type"] = "base64",
                            ["media_type"] = binary.MediaType,
                            ["data"] = binary.Data,
                        },
                    });
                    break;
                case ResourceContent resource:
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = $"[resource media_type={resource.MediaType}] {resource.Uri}",
                    });
                    break;
                case BinaryContent binary:
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = $"[binary media_type={binary.MediaType} data_omitted]",
                    });
                    break;
            }
        }

        return blocks;
    }

    private static object ProjectToolResultContent(IEnumerable<AgentContent> content)
    {
        var blocks = new List<object>();
        foreach (var item in content)
        {
            switch (item)
            {
                case TextContent text:
                    blocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = text.Text });
                    break;
                case JsonContent json:
                    blocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = json.Json });
                    break;
                case BinaryContent binary when binary.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "image",
                        ["source"] = new Dictionary<string, object?>
                        {
                            ["type"] = "base64",
                            ["media_type"] = binary.MediaType,
                            ["data"] = binary.Data,
                        },
                    });
                    break;
            }
        }

        return blocks.Count switch
        {
            0 => "(no tool output)",
            1 when blocks[0] is Dictionary<string, object?> value
                   && value["type"] as string == "text" => value["text"]!,
            _ => blocks,
        };
    }

    private IReadOnlyList<object> ProjectTools(
        IEnumerable<ToolDefinition> tools,
        object? cacheControl,
        bool deferLoading)
    {
        var projected = tools.Select(tool =>
        {
            var strict = tool.ConstrainedSampling?.Kind == ToolConstrainedSamplingKind.JsonSchema;
            if (strict
                && tool.ConstrainedSampling?.Strictness == ToolSchemaStrictness.Require
                && !_options.SupportsStrictTools)
            {
                throw new InvalidOperationException(
                    $"Tool '{tool.Name}' requires strict JSON-schema sampling, but the endpoint does not support it.");
            }

            using var schemaDocument = JsonDocument.Parse(tool.InputSchemaJson);
            var schema = schemaDocument.RootElement;
            object inputSchema;
            if (strict && _options.SupportsStrictTools)
            {
                inputSchema = schema.Clone();
            }
            else
            {
                inputSchema = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = schema.TryGetProperty("properties", out var properties)
                        ? properties.Clone()
                        : new Dictionary<string, object?>(),
                    ["required"] = schema.TryGetProperty("required", out var required)
                        ? required.Clone()
                        : Array.Empty<string>(),
                };
            }

            var value = new Dictionary<string, object?>
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = inputSchema,
            };
            if (_options.SupportsEagerToolInputStreaming)
            {
                value["eager_input_streaming"] = true;
            }

            if (strict && _options.SupportsStrictTools)
            {
                value["strict"] = true;
            }

            if (deferLoading)
            {
                value["defer_loading"] = true;
            }

            return value;
        }).Cast<object>().ToArray();
        if (cacheControl is not null
            && _options.SupportsCacheControlOnTools
            && projected.LastOrDefault() is Dictionary<string, object?> last)
        {
            last["cache_control"] = cacheControl;
        }

        return projected;
    }

    private ToolPlacement SplitTools(
        IReadOnlyList<ToolDefinition> tools,
        IReadOnlyList<AgentMessage> messages)
    {
        if (!_options.SupportsToolReferences)
        {
            return new ToolPlacement(tools, new Dictionary<string, ToolDefinition>(StringComparer.Ordinal));
        }

        var used = messages.Where(message => message.Role == AgentRole.Assistant)
            .SelectMany(message => message.Content.OfType<ToolCallContent>())
            .Select(call => call.Name)
            .ToHashSet(StringComparer.Ordinal);
        var deferredNames = messages.Where(message => message.Role == AgentRole.Tool)
            .SelectMany(message => message.AddedToolNames)
            .Where(name => !used.Contains(name))
            .ToHashSet(StringComparer.Ordinal);
        var unique = tools.GroupBy(tool => tool.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var deferred = unique.Where(pair => deferredNames.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var immediate = unique.Where(pair => !deferredNames.Contains(pair.Key)).Select(pair => pair.Value).ToArray();
        if (immediate.Length == 0 && deferred.Count > 0)
        {
            return new ToolPlacement(deferred.Values.ToArray(), new Dictionary<string, ToolDefinition>(StringComparer.Ordinal));
        }

        return new ToolPlacement(immediate, deferred);
    }

    private object? CacheControl(ModelCacheRetention retention)
    {
        if (retention == ModelCacheRetention.None)
        {
            return null;
        }

        var value = new Dictionary<string, object?> { ["type"] = "ephemeral" };
        if (retention == ModelCacheRetention.Long && _options.SupportsLongCacheRetention)
        {
            value["ttl"] = "1h";
        }

        return value;
    }

    private void ApplyHeaders(HttpRequestMessage request, string? apiKey, ModelRequest modelRequest)
    {
        ValidateCredential(apiKey, nameof(AnthropicMessagesProviderOptions.GetApiKeyAsync));
        foreach (var header in _headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (!request.Headers.Contains("anthropic-version"))
        {
            request.Headers.TryAddWithoutValidation("anthropic-version", _options.ApiVersion);
        }

        var oauth = apiKey?.Contains("sk-ant-oat", StringComparison.Ordinal) == true;
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.TryAddWithoutValidation(oauth ? "Authorization" : "x-api-key", oauth ? "Bearer " + apiKey : apiKey);
        }

        var beta = new List<string>();
        if (!_options.SupportsEagerToolInputStreaming && modelRequest.Tools.Count > 0)
        {
            beta.Add(FineGrainedToolStreamingBeta);
        }

        if (_options.InterleavedThinking && !_options.ForceAdaptiveThinking)
        {
            beta.Add(InterleavedThinkingBeta);
        }

        if (oauth)
        {
            beta.Insert(0, "oauth-2025-04-20");
            beta.Insert(0, "claude-code-20250219");
            request.Headers.TryAddWithoutValidation("x-app", "cli");
        }

        if (beta.Count > 0 && !request.Headers.Contains("anthropic-beta"))
        {
            request.Headers.TryAddWithoutValidation("anthropic-beta", string.Join(",", beta));
        }

        if (_options.SendSessionAffinityHeaders
            && modelRequest.Parameters.CacheRetention != ModelCacheRetention.None
            && modelRequest.SessionId is { } sessionId)
        {
            request.Headers.TryAddWithoutValidation("x-session-affinity", sessionId);
        }
    }

    private static string NormalizeToolCallId(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, 64));
        foreach (var character in value)
        {
            if (builder.Length == 64)
            {
                break;
            }

            builder.Append(char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_');
        }

        return builder.Length == 0 ? "tool_call" : builder.ToString();
    }

    private static JsonElement ParseRequiredObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Tool arguments must be a JSON object.");
        }

        return document.RootElement.Clone();
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

    private static void ValidateOptions(AnthropicMessagesProviderOptions options)
    {
        if (!options.Endpoint.IsAbsoluteUri
            || options.Endpoint.UserInfo.Length > 0
            || (options.Endpoint.Scheme != Uri.UriSchemeHttp && options.Endpoint.Scheme != Uri.UriSchemeHttps)
            || (options.Endpoint.Scheme == Uri.UriSchemeHttp && !options.Endpoint.IsLoopback && !options.AllowInsecureHttp))
        {
            throw new ArgumentException("The endpoint must be a permitted absolute HTTP or HTTPS URI without credentials.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.ProviderId)
            || string.IsNullOrWhiteSpace(options.ApiId)
            || string.IsNullOrWhiteSpace(options.ApiVersion)
            || !Enum.IsDefined(typeof(AnthropicThinkingDisplay), options.ThinkingDisplay)
            || options.DefaultMaxOutputTokens < 1
            || options.MaxEventCharacters is < 1 or > 100_000_000
            || options.MaxErrorCharacters is < 1 or > 10_000_000
            || options.MaxRequestBytes is < 2 or > 100_000_000
            || options.MaxResponseCharacters is < 1 or > 100_000_000
            || options.MaxToolCallsPerResponse is < 1 or > 10_000)
        {
            throw new ArgumentException("One or more Anthropic provider identifiers or bounds are invalid.", nameof(options));
        }

        ValidateCredential(options.ApiKey, nameof(options));
        foreach (var header in options.Headers)
        {
            if (string.IsNullOrWhiteSpace(header.Key)
                || header.Key.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0
                || header.Value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            {
                throw new ArgumentException("An Anthropic header is invalid.", nameof(options));
            }
        }
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

    private static async IAsyncEnumerable<SseEvent> ReadSseEventsAsync(
        StreamReader reader,
        int maximumCharacters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var eventName = string.Empty;
        var data = new StringBuilder();
        await foreach (var line in ReadBoundedLinesAsync(reader, maximumCharacters, cancellationToken))
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new SseEvent(eventName, data.ToString());
                }

                eventName = string.Empty;
                data.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line.Substring(6).TrimStart();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line.Substring(5).TrimStart());
                if (data.Length > maximumCharacters)
                {
                    throw new InvalidDataException("An Anthropic SSE event exceeded the configured size limit.");
                }
            }
        }

        if (data.Length > 0)
        {
            yield return new SseEvent(eventName, data.ToString());
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
                            throw new InvalidDataException("An Anthropic SSE line exceeded the configured size limit.");
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

    private sealed class MessageProjection
    {
        public MessageProjection(string role, IEnumerable<object> content)
        {
            Role = role;
            Content = content.ToList();
        }

        public string Role { get; }

        public List<object> Content { get; }
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

    private sealed class SseEvent
    {
        public SseEvent(string name, string data)
        {
            Name = name;
            Data = data;
        }

        public string Name { get; }

        public string Data { get; }
    }
}
