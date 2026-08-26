using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.Runtime.Documents;
using OpenGameAgent.Kernel;
using OpenGameAgent.ProviderTransport;

namespace OpenGameAgent.Providers.Bedrock;

public enum BedrockToolChoice
{
    Auto,
    Any,
    None,
    Tool,
}

public enum BedrockThinkingDisplay
{
    Summarized,
    Omitted,
}

public sealed class BedrockConverseProviderOptions
{
    public IAmazonBedrockRuntime? Client { get; set; }

    public BedrockConverseTransport? Transport { get; set; }

    public string ProviderId { get; set; } = "amazon-bedrock";

    public string ApiId { get; set; } = "bedrock-converse-stream";

    public string? Region { get; set; }

    public string? Profile { get; set; }

    public string? ServiceUrl { get; set; }

    public bool AllowInsecureHttp { get; set; }

    public string? AccessKeyId { get; set; }

    public string? SecretAccessKey { get; set; }

    public string? SessionToken { get; set; }

    public string? BearerToken { get; set; }

    public Func<string, string?>? ModelDisplayNameResolver { get; set; }

    public bool SkipAuthentication { get; set; }

    public bool SupportsStrictTools { get; set; }

    public bool ForcePromptCaching { get; set; }

    public BedrockToolChoice? ToolChoice { get; set; }

    public string? RequiredToolName { get; set; }

    public bool InterleavedThinking { get; set; } = true;

    public BedrockThinkingDisplay ThinkingDisplay { get; set; } = BedrockThinkingDisplay.Summarized;

    public IDictionary<string, string> RequestMetadata { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IDictionary<string, string?> Headers { get; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    public ProviderResponseObserver? ResponseObserver { get; set; }

    public int ResponseObserverTimeoutMilliseconds { get; set; } =
        ProviderResponseObserverRunner.DefaultTimeoutMilliseconds;

    public int MaxResponseCharacters { get; set; } = 16_000_000;

    public int MaxToolCallsPerResponse { get; set; } = 256;
}

public sealed class BedrockConverseProvider : IModelProvider, IModelProviderCapabilities
{
    private const string EmptyText = "<empty>";
    private readonly BedrockConverseProviderOptions _options;
    private readonly IReadOnlyDictionary<string, string> _requestMetadata;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly ProviderResponseObserver? _responseObserver;
    private readonly int _responseObserverTimeoutMilliseconds;
    private readonly IReadOnlyCollection<string> _supportedApis;

    public BedrockConverseProvider(BedrockConverseProviderOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
        _requestMetadata = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(options.RequestMetadata, StringComparer.Ordinal));
        _headers = NormalizeHeaders(options.Headers);
        _responseObserver = options.ResponseObserver;
        _responseObserverTimeoutMilliseconds = options.ResponseObserverTimeoutMilliseconds;
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
            throw new NotSupportedException("Bedrock ConverseStream uses the AWS event-stream transport.");
        }

        var protocolRequest = BuildRequest(request);
        var state = new BedrockStreamState(
            request.Model,
            _options.ProviderId,
            _options.ApiId,
            _options.MaxResponseCharacters,
            _options.MaxToolCallsPerResponse);
        yield return ModelStreamEvent.Update(ModelStreamEventKind.Started, state.Partial());

        IAmazonBedrockRuntime? ownedClient = null;
        var transport = _options.Transport;
        if (transport is null)
        {
            var client = _options.Client;
            if (client is null)
            {
                client = CreateClient(request.Model);
                ownedClient = client;
            }

            transport = (value, token) => AwsBedrockTransport.StreamAsync(
                client,
                value,
                _headers,
                _options.ProviderId,
                _options.ApiId,
                request.Model,
                _responseObserver,
                _responseObserverTimeoutMilliseconds,
                _options.MaxResponseCharacters,
                token);
        }

        try
        {
            await foreach (var item in transport(protocolRequest, cancellationToken).WithCancellation(cancellationToken))
            {
                foreach (var update in state.Apply(item))
                {
                    yield return update;
                }
            }

            yield return ModelStreamEvent.Terminal(state.Complete());
        }
        finally
        {
            ownedClient?.Dispose();
        }
    }

    internal ConverseStreamRequest BuildRequest(ModelRequest request)
    {
        var modelIdentity = string.Join(
            " ",
            new[] { request.Model, _options.ModelDisplayNameResolver?.Invoke(request.Model) }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        var messages = ProviderTranscript.Normalize(
            request.Messages,
            _options.ProviderId,
            _options.ApiId,
            request.Model,
            (id, _, _, _) => NormalizeToolCallId(id));
        var cache = request.Parameters.CacheRetention != ModelCacheRetention.None && SupportsPromptCaching(modelIdentity);
        var additionalFields = BuildAdditionalFields(modelIdentity, request.Parameters);
        var result = new ConverseStreamRequest
        {
            ModelId = request.Model,
            Messages = ProjectMessages(messages, modelIdentity, cache, request.Parameters.CacheRetention),
            InferenceConfig = BuildInferenceConfig(request.Parameters),
            System = BuildSystem(request.SystemPrompt, cache, request.Parameters.CacheRetention),
            ToolConfig = BuildToolConfiguration(request.Tools),
            RequestMetadata = _requestMetadata.Count > 0
                ? new Dictionary<string, string>(_requestMetadata, StringComparer.Ordinal)
                : null,
        };
        if (additionalFields.HasValue)
        {
            result.AdditionalModelRequestFields = additionalFields.Value;
        }

        return result;
    }

    private List<Message> ProjectMessages(
        IReadOnlyList<AgentMessage> messages,
        string model,
        bool cache,
        ModelCacheRetention retention)
    {
        var result = new List<Message>();
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role is AgentRole.User or AgentRole.Custom)
            {
                result.Add(new Message
                {
                    Role = ConversationRole.User,
                    Content = ProjectUserContent(message.Content),
                });
                continue;
            }

            if (message.Role == AgentRole.Assistant)
            {
                var content = ProjectAssistantContent(message.Content, model, _options.MaxResponseCharacters);
                if (content.Count > 0)
                {
                    result.Add(new Message { Role = ConversationRole.Assistant, Content = content });
                }

                continue;
            }

            if (message.Role == AgentRole.Tool)
            {
                var content = new List<ContentBlock>();
                while (index < messages.Count && messages[index].Role == AgentRole.Tool)
                {
                    var tool = messages[index];
                    content.Add(new ContentBlock
                    {
                        ToolResult = new ToolResultBlock
                        {
                            ToolUseId = tool.ToolCallId,
                            Status = tool.IsError ? ToolResultStatus.Error : ToolResultStatus.Success,
                            Content = ProjectToolResultContent(tool.Content),
                        },
                    });
                    index++;
                }

                index--;
                result.Add(new Message { Role = ConversationRole.User, Content = content });
            }
        }

        if (cache && result.LastOrDefault() is { Role: var role, Content: { } lastContent } && role == ConversationRole.User)
        {
            lastContent.Add(new ContentBlock { CachePoint = CachePoint(retention) });
        }

        return result;
    }

    private static List<ContentBlock> ProjectUserContent(IEnumerable<AgentContent> content)
    {
        var result = new List<ContentBlock>();
        foreach (var item in content)
        {
            switch (item)
            {
                case TextContent text when NonBlankText(text.Text) is { } sanitized:
                    result.Add(new ContentBlock { Text = sanitized });
                    break;
                case JsonContent json:
                    result.Add(new ContentBlock { Text = json.Json });
                    break;
                case BinaryContent binary when binary.MediaKind == AgentMediaKind.Image:
                    result.Add(new ContentBlock { Image = Image(binary) });
                    break;
                case ResourceContent resource:
                    result.Add(new ContentBlock { Text = $"[resource media_type={resource.MediaType}] {resource.Uri}" });
                    break;
            }
        }

        if (result.Count == 0)
        {
            result.Add(new ContentBlock { Text = EmptyText });
        }

        return result;
    }

    private static List<ContentBlock> ProjectAssistantContent(
        IEnumerable<AgentContent> content,
        string model,
        int maximumResponseCharacters)
    {
        var result = new List<ContentBlock>();
        foreach (var item in content)
        {
            switch (item)
            {
                case TextContent text when NonBlankText(text.Text) is { } sanitized:
                    result.Add(new ContentBlock { Text = sanitized });
                    break;
                case ReasoningContent { Redacted: true } redacted:
                    if (string.IsNullOrWhiteSpace(redacted.Signature))
                    {
                        throw new InvalidDataException("Redacted Bedrock reasoning is missing its opaque content.");
                    }

                    if (redacted.Signature.Length > maximumResponseCharacters)
                    {
                        throw new InvalidDataException("Redacted Bedrock reasoning exceeded the configured response limit.");
                    }

                    byte[] opaque;
                    try
                    {
                        opaque = Convert.FromBase64String(redacted.Signature);
                    }
                    catch (FormatException exception)
                    {
                        throw new InvalidDataException("Redacted Bedrock reasoning has invalid opaque content.", exception);
                    }

                    if (opaque.Length == 0)
                    {
                        throw new InvalidDataException("Redacted Bedrock reasoning is empty.");
                    }

                    result.Add(new ContentBlock
                    {
                        ReasoningContent = new ReasoningContentBlock
                        {
                            RedactedContent = new MemoryStream(opaque, writable: false),
                        },
                    });
                    break;
                case ReasoningContent reasoning when NonBlankText(reasoning.Text) is { } thinking:
                    if (IsClaude(model) && string.IsNullOrWhiteSpace(reasoning.Signature))
                    {
                        result.Add(new ContentBlock { Text = thinking });
                    }
                    else
                    {
                        result.Add(new ContentBlock
                        {
                            ReasoningContent = new ReasoningContentBlock
                            {
                                ReasoningText = new ReasoningTextBlock
                                {
                                    Text = thinking,
                                    Signature = IsClaude(model) ? reasoning.Signature : null,
                                },
                            },
                        });
                    }

                    break;
                case ToolCallContent call:
                    result.Add(new ContentBlock
                    {
                        ToolUse = new ToolUseBlock
                        {
                            ToolUseId = call.Id,
                            Name = call.Name,
                            Input = Document.FromObject(ParsePlainObject(call.ArgumentsJson)),
                        },
                    });
                    break;
            }
        }

        return result;
    }

    private static List<ToolResultContentBlock> ProjectToolResultContent(IEnumerable<AgentContent> content)
    {
        var result = new List<ToolResultContentBlock>();
        foreach (var item in content)
        {
            switch (item)
            {
                case TextContent text when NonBlankText(text.Text) is { } sanitized:
                    result.Add(new ToolResultContentBlock { Text = sanitized });
                    break;
                case JsonContent json:
                    result.Add(new ToolResultContentBlock { Text = json.Json });
                    break;
                case BinaryContent binary when binary.MediaKind == AgentMediaKind.Image:
                    result.Add(new ToolResultContentBlock { Image = Image(binary) });
                    break;
            }
        }

        if (result.Count == 0)
        {
            result.Add(new ToolResultContentBlock { Text = EmptyText });
        }

        return result;
    }

    private ToolConfiguration? BuildToolConfiguration(IReadOnlyList<ToolDefinition> tools)
    {
        if (tools.Count == 0 || _options.ToolChoice == BedrockToolChoice.None)
        {
            return null;
        }

        var values = new List<Amazon.BedrockRuntime.Model.Tool>();
        foreach (var tool in tools)
        {
            if (tool.ConstrainedSampling?.Kind == ToolConstrainedSamplingKind.Grammar)
            {
                throw new NotSupportedException("Bedrock Converse tools do not support grammar-constrained sampling.");
            }

            var strict = tool.ConstrainedSampling?.Kind == ToolConstrainedSamplingKind.JsonSchema;
            if (strict
                && tool.ConstrainedSampling!.Strictness == ToolSchemaStrictness.Require
                && !_options.SupportsStrictTools)
            {
                throw new NotSupportedException("This Bedrock model does not support required strict tool sampling.");
            }

            values.Add(new Amazon.BedrockRuntime.Model.Tool
            {
                ToolSpec = new ToolSpecification
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    InputSchema = new ToolInputSchema
                    {
                        Json = Document.FromObject(ParsePlainObject(tool.InputSchemaJson)),
                    },
                    Strict = strict && _options.SupportsStrictTools ? true : null,
                },
            });
        }

        return new ToolConfiguration
        {
            Tools = values,
            ToolChoice = _options.ToolChoice switch
            {
                BedrockToolChoice.Auto => new ToolChoice { Auto = new AutoToolChoice() },
                BedrockToolChoice.Any => new ToolChoice { Any = new AnyToolChoice() },
                BedrockToolChoice.Tool => new ToolChoice { Tool = new SpecificToolChoice { Name = _options.RequiredToolName } },
                _ => null,
            },
        };
    }

    private static InferenceConfiguration BuildInferenceConfig(ModelParameters parameters)
    {
        var config = new InferenceConfiguration
        {
            MaxTokens = parameters.MaxOutputTokens,
            Temperature = parameters.Temperature is { } temperature ? (float)temperature : null,
        };
        if (parameters.SamplingParametersJson is { } json)
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("topP", out var topP)
                || document.RootElement.TryGetProperty("top_p", out topP))
            {
                config.TopP = topP.GetSingle();
            }

            if (document.RootElement.TryGetProperty("stopSequences", out var stops)
                || document.RootElement.TryGetProperty("stop_sequences", out stops))
            {
                config.StopSequences = stops.EnumerateArray().Select(value => value.GetString()!).ToList();
            }
        }

        return config;
    }

    private static List<SystemContentBlock>? BuildSystem(
        string systemPrompt,
        bool cache,
        ModelCacheRetention retention)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            return null;
        }

        var result = new List<SystemContentBlock> { new() { Text = SanitizeUnicode(systemPrompt) } };
        if (cache)
        {
            result.Add(new SystemContentBlock { CachePoint = CachePoint(retention) });
        }

        return result;
    }

    private Document? BuildAdditionalFields(string model, ModelParameters parameters)
    {
        var fields = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(parameters.ReasoningLevel)
            && !string.Equals(parameters.ReasoningLevel, "off", StringComparison.OrdinalIgnoreCase)
            && IsClaude(model))
        {
            var level = parameters.ReasoningLevel!.ToLowerInvariant();
            var display = IsGovCloud(model, _options.Region)
                ? null
                : _options.ThinkingDisplay == BedrockThinkingDisplay.Omitted ? "omitted" : "summarized";
            if (SupportsAdaptiveThinking(model))
            {
                fields["thinking"] = new Dictionary<string, object?>
                {
                    ["type"] = "adaptive",
                    ["display"] = display,
                }.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value);
                fields["output_config"] = new Dictionary<string, object?>
                {
                    ["effort"] = MapEffort(model, level),
                };
            }
            else
            {
                var budgetLevel = level is "xhigh" or "max" ? "high" : level;
                var budget = parameters.ReasoningBudgets.TryGetValue(budgetLevel, out var custom)
                    ? custom
                    : budgetLevel switch
                    {
                        "minimal" => 1024,
                        "low" => 2048,
                        "medium" => 8192,
                        _ => 16384,
                    };
                fields["thinking"] = new Dictionary<string, object?>
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = budget,
                    ["display"] = display,
                }.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value);
                if (_options.InterleavedThinking)
                {
                    fields["anthropic_beta"] = new[] { "interleaved-thinking-2025-05-14" };
                }
            }
        }

        foreach (var extension in parameters.Extensions)
        {
            if (fields.ContainsKey(extension.Key))
            {
                throw new InvalidOperationException($"Model extension '{extension.Key}' cannot override a core Bedrock field.");
            }

            fields[extension.Key] = ParseJsonOrString(extension.Value);
        }

        return fields.Count == 0 ? (Document?)null : Document.FromObject(fields);
    }

    private IAmazonBedrockRuntime CreateClient(string model)
    {
        var config = new AmazonBedrockRuntimeConfig();
        var region = ResolveRegion(
            model,
            _options.Region,
            _options.ServiceUrl,
            Environment.GetEnvironmentVariable("AWS_REGION"),
            Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION"));
        config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        if (!string.IsNullOrWhiteSpace(_options.ServiceUrl))
        {
            config.ServiceURL = _options.ServiceUrl;
            config.AuthenticationRegion = region;
        }

        var bearerToken = _options.BearerToken
                          ?? Environment.GetEnvironmentVariable("AWS_BEARER_TOKEN_BEDROCK");
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            config.AWSTokenProvider = new StaticTokenProvider(bearerToken!);
            config.AuthSchemePreference = new List<string> { "httpBearerAuth" };
        }

        AWSCredentials? credentials = null;
        if (_options.SkipAuthentication)
        {
            credentials = new BasicAWSCredentials("dummy-access-key", "dummy-secret-key");
        }
        else if (!string.IsNullOrWhiteSpace(_options.Profile))
        {
            var chain = new CredentialProfileStoreChain();
            if (!chain.TryGetAWSCredentials(_options.Profile, out credentials))
            {
                throw new InvalidOperationException("AWS credential profile '" + _options.Profile + "' was not found.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(_options.AccessKeyId))
        {
            credentials = string.IsNullOrWhiteSpace(_options.SessionToken)
                ? new BasicAWSCredentials(_options.AccessKeyId, _options.SecretAccessKey)
                : new SessionAWSCredentials(_options.AccessKeyId, _options.SecretAccessKey, _options.SessionToken);
        }

        return credentials is null
            ? new CustomHeadersBedrockClient(config, _headers)
            : new CustomHeadersBedrockClient(credentials, config, _headers);
    }

    private bool SupportsPromptCaching(string model)
    {
        if (_options.ForcePromptCaching)
        {
            return true;
        }

        var value = NormalizeModel(model);
        if (!value.Contains("claude", StringComparison.Ordinal))
        {
            return false;
        }

        return value.Contains("-4-", StringComparison.Ordinal)
               || value.Contains("claude-3-7-sonnet", StringComparison.Ordinal)
               || value.Contains("claude-3-5-haiku", StringComparison.Ordinal)
               || value.Contains("fable-5", StringComparison.Ordinal)
               || value.Contains("opus-5", StringComparison.Ordinal)
               || value.Contains("sonnet-5", StringComparison.Ordinal);
    }

    private static CachePointBlock CachePoint(ModelCacheRetention retention) => new()
    {
        Type = CachePointType.Default,
        Ttl = retention == ModelCacheRetention.Long ? CacheTTL.ONE_HOUR : null,
    };

    private static ImageBlock Image(BinaryContent binary) => new()
    {
        Format = binary.MediaType.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ImageFormat.Jpeg,
            "image/png" => ImageFormat.Png,
            "image/gif" => ImageFormat.Gif,
            "image/webp" => ImageFormat.Webp,
            _ => throw new NotSupportedException("Unsupported Bedrock image type '" + binary.MediaType + "'."),
        },
        Source = new ImageSource { Bytes = new MemoryStream(Convert.FromBase64String(binary.Data), writable: false) },
    };

    private static object ParsePlainObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ToPlain(document.RootElement)!;
    }

    private static object? ToPlain(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(pair => pair.Name, pair => ToPlain(pair.Value), StringComparer.Ordinal),
        JsonValueKind.Array => value.EnumerateArray().Select(ToPlain).ToArray(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => null,
    };

    private static object ParseJsonOrString(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return ToPlain(document.RootElement)!;
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static string? NonBlankText(string value)
    {
        var sanitized = SanitizeUnicode(value);
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
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
                    builder.Append(value[index + 1]);
                }

                index++;
                continue;
            }

            if (!char.IsSurrogate(character))
            {
                builder?.Append(character);
                continue;
            }

            builder ??= new StringBuilder(value.Substring(0, index));
        }

        return builder?.ToString() ?? value;
    }

    private static string NormalizeToolCallId(string id)
    {
        var value = new string(id.Select(character => char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());
        return value.Length > 64 ? value.Substring(0, 64) : value;
    }

    private static bool IsClaude(string model) => NormalizeModel(model).Contains("claude", StringComparison.Ordinal);

    private static bool SupportsAdaptiveThinking(string model)
    {
        var value = NormalizeModel(model);
        return value.Contains("opus-4-6", StringComparison.Ordinal)
               || value.Contains("opus-4-7", StringComparison.Ordinal)
               || value.Contains("opus-4-8", StringComparison.Ordinal)
               || value.Contains("opus-5", StringComparison.Ordinal)
               || value.Contains("sonnet-4-6", StringComparison.Ordinal)
               || value.Contains("sonnet-5", StringComparison.Ordinal)
               || value.Contains("fable-5", StringComparison.Ordinal);
    }

    private static string MapEffort(string model, string level)
    {
        if (level == "xhigh" && (NormalizeModel(model).Contains("opus-4-7", StringComparison.Ordinal)
                                 || NormalizeModel(model).Contains("opus-4-8", StringComparison.Ordinal)
                                 || NormalizeModel(model).Contains("opus-5", StringComparison.Ordinal)
                                 || NormalizeModel(model).Contains("sonnet-5", StringComparison.Ordinal)))
        {
            return "xhigh";
        }

        return level switch
        {
            "minimal" or "low" => "low",
            "medium" => "medium",
            _ => "high",
        };
    }

    private static string NormalizeModel(string model) =>
        model.ToLowerInvariant().Replace('_', '-').Replace('.', '-').Replace(':', '-').Replace(' ', '-');

    private static bool IsGovCloud(string model, string? region) =>
        model.StartsWith("arn:aws-us-gov:", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("us-gov.", StringComparison.OrdinalIgnoreCase)
        || region?.StartsWith("us-gov-", StringComparison.OrdinalIgnoreCase) == true;

    private static string? RegionFromArn(string model)
    {
        if (!model.StartsWith("arn:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = model.Split(':');
        return parts.Length > 3 && parts[2] == "bedrock" ? parts[3] : null;
    }

    internal static string ResolveRegion(
        string model,
        string? configuredRegion,
        string? serviceUrl,
        string? environmentRegion,
        string? environmentDefaultRegion)
    {
        var endpointRegion = RegionFromServiceUrl(serviceUrl);
        return RegionFromArn(model)
               ?? configuredRegion
               ?? environmentRegion
               ?? environmentDefaultRegion
               ?? endpointRegion
               ?? "us-east-1";
    }

    private static string? RegionFromServiceUrl(string? serviceUrl)
    {
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var endpoint))
        {
            return null;
        }

        var pieces = endpoint.Host.Split('.');
        for (var index = 0; index + 1 < pieces.Length; index++)
        {
            if (string.Equals(pieces[index], "bedrock-runtime", StringComparison.OrdinalIgnoreCase))
            {
                return pieces[index + 1];
            }
        }

        return null;
    }

    private static void ValidateOptions(BedrockConverseProviderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProviderId) || string.IsNullOrWhiteSpace(options.ApiId))
        {
            throw new ArgumentException("Bedrock provider and API identifiers are required.", nameof(options));
        }

        if (!Enum.IsDefined(typeof(BedrockThinkingDisplay), options.ThinkingDisplay)
            || options.ToolChoice is { } choice && !Enum.IsDefined(typeof(BedrockToolChoice), choice))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.ToolChoice == BedrockToolChoice.Tool && string.IsNullOrWhiteSpace(options.RequiredToolName))
        {
            throw new ArgumentException("A Bedrock required tool name is missing.", nameof(options));
        }

        if (options.ToolChoice != BedrockToolChoice.Tool && options.RequiredToolName is not null)
        {
            throw new ArgumentException("Only specific tool choice can carry a required name.", nameof(options));
        }

        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            if (!Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out var endpoint)
                || endpoint.UserInfo.Length > 0
                || endpoint.Fragment.Length > 0
                || endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            {
                throw new ArgumentException(
                    "A Bedrock service URL must be an absolute HTTP or HTTPS URL without embedded credentials or a fragment.",
                    nameof(options));
            }

            if (endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback && !options.AllowInsecureHttp)
            {
                throw new ArgumentException(
                    "A remote Bedrock service URL must use HTTPS unless insecure HTTP is explicitly enabled.",
                    nameof(options));
            }
        }

        var hasAccess = !string.IsNullOrWhiteSpace(options.AccessKeyId);
        var hasSecret = !string.IsNullOrWhiteSpace(options.SecretAccessKey);
        if (hasAccess != hasSecret)
        {
            throw new ArgumentException("AWS access key ID and secret access key must be supplied together.", nameof(options));
        }

        if (options.MaxResponseCharacters <= 0
            || options.MaxToolCallsPerResponse <= 0
            || options.ResponseObserverTimeoutMilliseconds is < 1 or > 30_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Bedrock protocol limits must be positive.");
        }

        if (options.RequestMetadata.Count > 50
            || options.RequestMetadata.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                                                   || pair.Key.StartsWith("aws:", StringComparison.OrdinalIgnoreCase)
                                                   || pair.Key.Length > 64
                                                   || pair.Value is null
                                                   || pair.Value.Length > 256))
        {
            throw new ArgumentException("Bedrock request metadata is invalid.", nameof(options));
        }

        ProviderHeaderGuard.ValidateMerge(options.Headers, nameof(options));
    }

    private static bool IsReservedHeader(string key) =>
        key.Equals("authorization", StringComparison.OrdinalIgnoreCase)
        || key.Equals("host", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyDictionary<string, string> NormalizeHeaders(
        IEnumerable<KeyValuePair<string, string?>> headers) =>
        new ReadOnlyDictionary<string, string>(
            headers
                .Where(pair => pair.Value is not null && !IsReservedHeader(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase));

    private sealed class StaticTokenProvider : IAWSTokenProvider
    {
        private readonly AWSToken _token;

        public StaticTokenProvider(string token)
        {
            _token = new AWSToken { Token = token };
        }

        public Task<TryResponse<AWSToken>> TryResolveTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new TryResponse<AWSToken> { Success = true, Value = _token });
    }
}
