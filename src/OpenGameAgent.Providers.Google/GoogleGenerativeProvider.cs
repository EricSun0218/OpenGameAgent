using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Providers.Google;

public delegate ValueTask<string?> GoogleCredentialProvider(CancellationToken cancellationToken);

public enum GoogleApiFlavor
{
    Gemini,
    Vertex,
}

public enum GoogleCredentialPlacement
{
    ApiKeyHeader,
    BearerToken,
    None,
}

public enum GoogleToolChoice
{
    Auto,
    None,
    Any,
}

public sealed class GoogleGenerativeProviderOptions
{
    public GoogleGenerativeProviderOptions(HttpClient httpClient, Uri endpoint, GoogleApiFlavor flavor = GoogleApiFlavor.Gemini)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        Flavor = flavor;
        ProviderId = flavor == GoogleApiFlavor.Vertex ? "google-vertex" : "google";
        ApiId = flavor == GoogleApiFlavor.Vertex ? "google-vertex" : "google-generative-ai";
        CredentialPlacement = flavor == GoogleApiFlavor.Vertex
            ? GoogleCredentialPlacement.BearerToken
            : GoogleCredentialPlacement.ApiKeyHeader;
    }

    public HttpClient HttpClient { get; }

    public Uri Endpoint { get; }

    public GoogleApiFlavor Flavor { get; }

    public string ProviderId { get; set; }

    public string ApiId { get; set; }

    public string? Credential { get; set; }

    public GoogleCredentialProvider? GetCredentialAsync { get; set; }

    public GoogleCredentialPlacement CredentialPlacement { get; set; }

    public IDictionary<string, string> Headers { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public GoogleToolChoice? ToolChoice { get; set; }

    public bool SupportsImages { get; set; } = true;

    public bool UseLegacyOpenApiToolSchemas { get; set; }

    public bool AllowInsecureHttp { get; set; }

    public int MaxEventCharacters { get; set; } = 4_000_000;

    public int MaxErrorCharacters { get; set; } = 64_000;

    public int MaxRequestBytes { get; set; } = 16_000_000;

    public int MaxResponseCharacters { get; set; } = 16_000_000;

    public int MaxToolCallsPerResponse { get; set; } = 256;
}

public sealed class GoogleGenerativeProvider : IModelProvider, IModelProviderCapabilities
{
    private readonly GoogleGenerativeProviderOptions _options;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly IReadOnlyCollection<string> _supportedApis;

    public GoogleGenerativeProvider(GoogleGenerativeProviderOptions options)
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
            throw new NotSupportedException("This provider currently uses the Google server-sent-event transport.");
        }

        var endpoint = ResolveEndpoint(_options.Endpoint, request.Model);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        var credential = _options.GetCredentialAsync is null
            ? _options.Credential
            : await _options.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        ApplyHeaders(httpRequest, credential, request);
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
                $"The Google endpoint returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {error}",
                IsTransient(response),
                GetRetryAfter(response),
                (int)response.StatusCode);
        }

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var registration = cancellationToken.Register(stream.Dispose);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
        var state = new GoogleStreamState(
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

        foreach (var update in state.CloseOpenBlock())
        {
            yield return update;
        }

        yield return ModelStreamEvent.Terminal(state.Complete());
    }

    private byte[] SerializeRequest(ModelRequest request)
    {
        var requiresIds = RequiresToolCallId(request.Model);
        var messages = ProviderTranscript.Normalize(
            request.Messages,
            _options.ProviderId,
            _options.ApiId,
            request.Model,
            requiresIds ? (id, _, _, _) => NormalizeToolCallId(id) : null);
        var payload = new Dictionary<string, object?>
        {
            ["contents"] = ProjectMessages(messages, request.Model, requiresIds),
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            payload["systemInstruction"] = new Dictionary<string, object?>
            {
                ["parts"] = new object[]
                {
                    new Dictionary<string, object?> { ["text"] = SanitizeUnicode(request.SystemPrompt) },
                },
            };
        }

        var generationConfig = new Dictionary<string, object?>();
        if (request.Parameters.Temperature is { } temperature)
        {
            generationConfig["temperature"] = temperature;
        }

        if (request.Parameters.MaxOutputTokens is { } maxOutputTokens)
        {
            generationConfig["maxOutputTokens"] = maxOutputTokens;
        }

        ApplyThinking(generationConfig, request.Model, request.Parameters);
        MergeSampling(generationConfig, request.Parameters.SamplingParametersJson);
        if (generationConfig.Count > 0)
        {
            payload["generationConfig"] = generationConfig;
        }

        if (request.Tools.Count > 0)
        {
            payload["tools"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["functionDeclarations"] = ProjectTools(request.Tools, request.Model),
                },
            };
            var mode = ResolveToolMode(request.Tools, request.Model);
            if (mode is not null)
            {
                payload["toolConfig"] = new Dictionary<string, object?>
                {
                    ["functionCallingConfig"] = new Dictionary<string, object?> { ["mode"] = mode },
                };
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

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (bytes.Length > _options.MaxRequestBytes)
        {
            throw new InvalidOperationException("The Google request exceeded the configured byte limit.");
        }

        return bytes;
    }

    private IReadOnlyList<object> ProjectMessages(
        IReadOnlyList<AgentMessage> messages,
        string model,
        bool requiresIds)
    {
        var projected = new List<ProjectedContent>();
        foreach (var message in messages)
        {
            if (message.Role is AgentRole.User or AgentRole.Custom)
            {
                var parts = ProjectUserParts(message.Content);
                if (parts.Count > 0)
                {
                    projected.Add(new ProjectedContent("user", parts));
                }

                continue;
            }

            if (message.Role == AgentRole.Assistant)
            {
                var parts = ProjectAssistantParts(message.Content, requiresIds);
                if (parts.Count > 0)
                {
                    projected.Add(new ProjectedContent("model", parts));
                }

                continue;
            }

            if (message.Role == AgentRole.Tool)
            {
                ProjectToolResult(projected, message, model, requiresIds);
            }
        }

        return projected.Select(value => (object)new Dictionary<string, object?>
        {
            ["role"] = value.Role,
            ["parts"] = value.Parts,
        }).ToArray();
    }

    private IReadOnlyList<object> ProjectUserParts(IEnumerable<AgentContent> content)
    {
        var parts = new List<object>();
        foreach (var item in content)
        {
            switch (item)
            {
                case TextContent text:
                    parts.Add(new Dictionary<string, object?> { ["text"] = SanitizeUnicode(text.Text) });
                    break;
                case JsonContent json:
                    parts.Add(new Dictionary<string, object?> { ["text"] = json.Json });
                    break;
                case BinaryContent binary when binary.MediaKind == AgentMediaKind.Image && _options.SupportsImages:
                    parts.Add(InlineImage(binary));
                    break;
                case BinaryContent binary:
                    parts.Add(new Dictionary<string, object?>
                    {
                        ["text"] = $"(binary omitted: {binary.MediaType})",
                    });
                    break;
                case ResourceContent resource:
                    parts.Add(new Dictionary<string, object?>
                    {
                        ["text"] = $"[resource media_type={resource.MediaType}] {resource.Uri}",
                    });
                    break;
            }
        }

        return parts;
    }

    private static IReadOnlyList<object> ProjectAssistantParts(IEnumerable<AgentContent> content, bool requiresIds)
    {
        var parts = new List<object>();
        foreach (var item in content)
        {
            switch (item)
            {
                case TextContent text when text.Text.Length > 0 || IsValidSignature(text.Signature):
                    var textPart = new Dictionary<string, object?> { ["text"] = SanitizeUnicode(text.Text) };
                    AddSignature(textPart, text.Signature);
                    parts.Add(textPart);
                    break;
                case ReasoningContent reasoning when reasoning.Text.Length > 0 || IsValidSignature(reasoning.Signature):
                    var reasoningPart = new Dictionary<string, object?>
                    {
                        ["thought"] = true,
                        ["text"] = SanitizeUnicode(reasoning.Text),
                    };
                    AddSignature(reasoningPart, reasoning.Signature);
                    parts.Add(reasoningPart);
                    break;
                case ToolCallContent call:
                    using (var arguments = JsonDocument.Parse(call.ArgumentsJson))
                    {
                        var functionCall = new Dictionary<string, object?>
                        {
                            ["name"] = call.Name,
                            ["args"] = arguments.RootElement.Clone(),
                        };
                        if (requiresIds)
                        {
                            functionCall["id"] = call.Id;
                        }

                        var toolPart = new Dictionary<string, object?> { ["functionCall"] = functionCall };
                        AddSignature(toolPart, call.ThoughtSignature);
                        parts.Add(toolPart);
                    }

                    break;
            }
        }

        return parts;
    }

    private void ProjectToolResult(
        IList<ProjectedContent> projected,
        AgentMessage message,
        string model,
        bool requiresIds)
    {
        var text = string.Join("\n", message.Content.Select(item => item switch
        {
            TextContent value => value.Text,
            JsonContent value => value.Json,
            _ => null,
        }).Where(value => value is not null));
        var images = _options.SupportsImages
            ? message.Content.OfType<BinaryContent>()
                .Where(value => value.MediaKind == AgentMediaKind.Image)
                .ToArray()
            : Array.Empty<BinaryContent>();
        var supportsNestedImages = SupportsMultimodalFunctionResponse(model);
        var responseValue = text.Length > 0 ? SanitizeUnicode(text) : images.Length > 0 ? "(see attached image)" : string.Empty;
        var response = new Dictionary<string, object?>
        {
            [message.IsError ? "error" : "output"] = responseValue,
        };
        var functionResponse = new Dictionary<string, object?>
        {
            ["name"] = message.ToolName,
            ["response"] = response,
        };
        if (requiresIds)
        {
            functionResponse["id"] = message.ToolCallId;
        }

        if (images.Length > 0 && supportsNestedImages)
        {
            functionResponse["parts"] = images.Select(value => (object)InlineImage(value)).ToArray();
        }

        var part = new Dictionary<string, object?> { ["functionResponse"] = functionResponse };
        if (projected.LastOrDefault() is { Role: "user" } last
            && last.Parts.All(IsFunctionResponsePart))
        {
            last.Parts.Add(part);
        }
        else
        {
            projected.Add(new ProjectedContent("user", new[] { part }));
        }

        if (images.Length > 0 && !supportsNestedImages)
        {
            var imageParts = new List<object>
            {
                new Dictionary<string, object?> { ["text"] = "Tool result image:" },
            };
            imageParts.AddRange(images.Select(value => (object)InlineImage(value)));
            projected.Add(new ProjectedContent("user", imageParts));
        }
    }

    private IReadOnlyList<object> ProjectTools(IReadOnlyList<ToolDefinition> tools, string model)
    {
        var result = new List<object>(tools.Count);
        foreach (var tool in tools)
        {
            if (tool.ConstrainedSampling?.Kind == ToolConstrainedSamplingKind.Grammar)
            {
                throw new NotSupportedException("Google function declarations do not support grammar-constrained tools.");
            }

            using var schema = JsonDocument.Parse(tool.InputSchemaJson);
            var declaration = new Dictionary<string, object?>
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
            };
            if (_options.UseLegacyOpenApiToolSchemas)
            {
                declaration["parameters"] = SanitizeOpenApiSchema(schema.RootElement);
            }
            else
            {
                declaration["parametersJsonSchema"] = schema.RootElement.Clone();
            }

            result.Add(declaration);
        }

        return result;
    }

    private string? ResolveToolMode(IReadOnlyList<ToolDefinition> tools, string model)
    {
        var supportsStrict = SupportsStrictToolSampling(model);
        var strictRequested = tools.Any(tool => tool.ConstrainedSampling?.Kind == ToolConstrainedSamplingKind.JsonSchema);
        var strictRequired = tools.Any(tool => tool.ConstrainedSampling?.Kind == ToolConstrainedSamplingKind.JsonSchema
                                               && tool.ConstrainedSampling.Strictness == ToolSchemaStrictness.Require);
        if (strictRequired && !supportsStrict)
        {
            throw new NotSupportedException("The selected Google model does not support required JSON-schema tool sampling.");
        }

        if (_options.ToolChoice == GoogleToolChoice.None)
        {
            return "NONE";
        }

        if (_options.ToolChoice == GoogleToolChoice.Any)
        {
            return "ANY";
        }

        if (strictRequested && supportsStrict)
        {
            return "VALIDATED";
        }

        return _options.ToolChoice == GoogleToolChoice.Auto ? "AUTO" : null;
    }

    private static void ApplyThinking(IDictionary<string, object?> config, string model, ModelParameters parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.ReasoningLevel))
        {
            return;
        }

        var level = parameters.ReasoningLevel!.Trim().ToLowerInvariant();
        var thinking = new Dictionary<string, object?>();
        if (level == "off")
        {
            if (IsGemini3Pro(model))
            {
                thinking["thinkingLevel"] = "LOW";
            }
            else if (IsGemini3Flash(model) || IsGemma4(model))
            {
                thinking["thinkingLevel"] = "MINIMAL";
            }
            else
            {
                thinking["thinkingBudget"] = 0;
            }
        }
        else
        {
            thinking["includeThoughts"] = true;
            if (IsGemini3Pro(model) || IsGemini3Flash(model) || IsGemma4(model))
            {
                thinking["thinkingLevel"] = ResolveThinkingLevel(model, level);
            }
            else
            {
                thinking["thinkingBudget"] = ResolveThinkingBudget(model, level, parameters.ReasoningBudgets);
            }
        }

        config["thinkingConfig"] = thinking;
    }

    private static string ResolveThinkingLevel(string model, string level)
    {
        var normalized = level switch
        {
            "minimal" => "MINIMAL",
            "low" => "LOW",
            "medium" => "MEDIUM",
            "high" or "xhigh" or "max" => "HIGH",
            _ => throw new ArgumentException("Unsupported Google reasoning level '" + level + "'."),
        };
        if (IsGemini3Pro(model))
        {
            return normalized is "MINIMAL" or "LOW" ? "LOW" : "HIGH";
        }

        if (IsGemma4(model))
        {
            return normalized is "MINIMAL" or "LOW" ? "MINIMAL" : "HIGH";
        }

        return normalized;
    }

    private static int ResolveThinkingBudget(
        string model,
        string level,
        IReadOnlyDictionary<string, int> customBudgets)
    {
        if (customBudgets.TryGetValue(level, out var custom))
        {
            return custom;
        }

        var normalized = level is "xhigh" or "max" ? "high" : level;
        return (model.ToLowerInvariant(), normalized) switch
        {
            (var id, "minimal") when id.Contains("2.5-flash-lite", StringComparison.Ordinal) => 512,
            (var id, "minimal") when id.Contains("2.5", StringComparison.Ordinal) => 128,
            (var id, "low") when id.Contains("2.5", StringComparison.Ordinal) => 2048,
            (var id, "medium") when id.Contains("2.5", StringComparison.Ordinal) => 8192,
            (var id, "high") when id.Contains("2.5-pro", StringComparison.Ordinal) => 32768,
            (var id, "high") when id.Contains("2.5-flash", StringComparison.Ordinal) => 24576,
            (_, "minimal" or "low" or "medium" or "high") => -1,
            _ => throw new ArgumentException("Unsupported Google reasoning level '" + level + "'."),
        };
    }

    private static void MergeSampling(IDictionary<string, object?> config, string? json)
    {
        if (json is null)
        {
            return;
        }

        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (config.ContainsKey(property.Name))
            {
                throw new InvalidOperationException($"Sampling parameter '{property.Name}' cannot override a core request field.");
            }

            config[property.Name] = property.Value.Clone();
        }
    }

    private void ApplyHeaders(HttpRequestMessage httpRequest, string? credential, ModelRequest request)
    {
        if (_options.CredentialPlacement != GoogleCredentialPlacement.None && string.IsNullOrWhiteSpace(credential))
        {
            throw new InvalidOperationException("A Google credential is required.");
        }

        foreach (var header in _headers)
        {
            if (!httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                throw new InvalidOperationException($"Google request header '{header.Key}' is invalid.");
            }
        }

        if (_options.CredentialPlacement == GoogleCredentialPlacement.ApiKeyHeader)
        {
            httpRequest.Headers.TryAddWithoutValidation("x-goog-api-key", credential);
        }
        else if (_options.CredentialPlacement == GoogleCredentialPlacement.BearerToken)
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            httpRequest.Headers.TryAddWithoutValidation("x-goog-request-params", "session_id=" + request.SessionId);
        }
    }

    private static Uri ResolveEndpoint(Uri template, string model)
    {
        var value = template.OriginalString.Replace("{model}", Uri.EscapeDataString(model), StringComparison.Ordinal);
        if (!value.Contains("alt=", StringComparison.OrdinalIgnoreCase))
        {
            value += value.Contains('?', StringComparison.Ordinal) ? "&alt=sse" : "?alt=sse";
        }

        return new Uri(value, UriKind.Absolute);
    }

    private static Dictionary<string, object?> InlineImage(BinaryContent binary) => new()
    {
        ["inlineData"] = new Dictionary<string, object?>
        {
            ["mimeType"] = binary.MediaType,
            ["data"] = binary.Data,
        },
    };

    private static bool IsFunctionResponsePart(object value) =>
        value is Dictionary<string, object?> dictionary && dictionary.ContainsKey("functionResponse");

    private static void AddSignature(IDictionary<string, object?> part, string? signature)
    {
        if (IsValidSignature(signature))
        {
            part["thoughtSignature"] = signature;
        }
    }

    private static bool IsValidSignature(string? signature)
    {
        if (string.IsNullOrEmpty(signature) || signature!.Length % 4 != 0)
        {
            return false;
        }

        try
        {
            Convert.FromBase64String(signature);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool RequiresToolCallId(string model)
    {
        var lower = model.ToLowerInvariant();
        return lower.StartsWith("claude-", StringComparison.Ordinal)
               || lower.StartsWith("gpt-oss-", StringComparison.Ordinal)
               || GeminiMajorVersion(lower) is >= 3;
    }

    private static bool SupportsMultimodalFunctionResponse(string model)
    {
        var major = GeminiMajorVersion(model.ToLowerInvariant());
        return major is null or >= 3;
    }

    private static bool SupportsStrictToolSampling(string model) => GeminiMajorVersion(model.ToLowerInvariant()) is >= 3;

    private static int? GeminiMajorVersion(string model)
    {
        var prefix = model.StartsWith("gemini-live-", StringComparison.Ordinal)
            ? "gemini-live-"
            : model.StartsWith("gemini-", StringComparison.Ordinal) ? "gemini-" : null;
        if (prefix is null)
        {
            return null;
        }

        var start = prefix.Length;
        var end = start;
        while (end < model.Length && char.IsDigit(model[end]))
        {
            end++;
        }

        return end > start && int.TryParse(model.Substring(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string NormalizeToolCallId(string id)
    {
        var builder = new StringBuilder(Math.Min(id.Length, 64));
        foreach (var character in id)
        {
            if (builder.Length >= 64)
            {
                break;
            }

            builder.Append(char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_');
        }

        return builder.Length == 0 ? "call" : builder.ToString();
    }

    private static bool IsGemini3Pro(string model) =>
        model.ToLowerInvariant().StartsWith("gemini-3", StringComparison.Ordinal)
        && model.Contains("pro", StringComparison.OrdinalIgnoreCase);

    private static bool IsGemini3Flash(string model)
    {
        var lower = model.ToLowerInvariant();
        return (lower.StartsWith("gemini-3", StringComparison.Ordinal) && lower.Contains("flash", StringComparison.Ordinal))
               || lower is "gemini-flash-latest" or "gemini-flash-lite-latest";
    }

    private static bool IsGemma4(string model) =>
        model.Contains("gemma-4", StringComparison.OrdinalIgnoreCase)
        || model.Contains("gemma4", StringComparison.OrdinalIgnoreCase);

    private static object? SanitizeOpenApiSchema(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var result = new Dictionary<string, object?>();
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is "$schema" or "$id" or "$anchor" or "$dynamicAnchor" or "$vocabulary" or "$comment" or "$defs" or "definitions")
                {
                    continue;
                }

                result[property.Name] = SanitizeOpenApiSchema(property.Value);
            }

            return result;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().Select(SanitizeOpenApiSchema).ToArray();
        }

        return value.Clone();
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
            if (char.IsHighSurrogate(character)
                && index + 1 < value.Length
                && char.IsLowSurrogate(value[index + 1]))
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
                    throw new InvalidDataException("A Google SSE event exceeded the configured size limit.");
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
                            throw new InvalidDataException("A Google SSE line exceeded the configured size limit.");
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

    private static void ValidateOptions(GoogleGenerativeProviderOptions options)
    {
        if (!Enum.IsDefined(typeof(GoogleApiFlavor), options.Flavor)
            || !Enum.IsDefined(typeof(GoogleCredentialPlacement), options.CredentialPlacement)
            || options.ToolChoice is { } choice && !Enum.IsDefined(typeof(GoogleToolChoice), choice))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.ProviderId) || string.IsNullOrWhiteSpace(options.ApiId))
        {
            throw new ArgumentException("Google provider and API identifiers are required.", nameof(options));
        }

        if (!options.AllowInsecureHttp && options.Endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The Google endpoint must use HTTPS.", nameof(options));
        }

        if (options.MaxEventCharacters <= 0
            || options.MaxErrorCharacters <= 0
            || options.MaxRequestBytes <= 0
            || options.MaxResponseCharacters <= 0
            || options.MaxToolCallsPerResponse <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Google protocol limits must be positive.");
        }
    }

    private sealed class ProjectedContent
    {
        public ProjectedContent(string role, IEnumerable<object> parts)
        {
            Role = role;
            Parts = parts.ToList();
        }

        public string Role { get; }

        public List<object> Parts { get; }
    }
}
