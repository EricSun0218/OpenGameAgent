using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Providers.Native;

public sealed class GeminiInteractionsStreamingProvider :
    IStreamingModelProvider,
    IPreparedStreamingModelProvider,
    IProviderRouteMetadataSource
{
    private const string ContentType = "application/json; charset=utf-8";
    private const string PolicyVersion = "gemini-interactions.route-policy.v1";

    private readonly GeminiInteractionsProviderOptions _options;
    private readonly INativeApiCredentialSource _credentials;
    private readonly INativeProviderHttpTransport _transport;
    private readonly ProviderCapabilities _capabilities;
    private readonly ProviderRouteMetadata _metadata;
    private readonly Uri _endpoint;

    public GeminiInteractionsStreamingProvider(
        GeminiInteractionsProviderOptions options,
        INativeApiCredentialSource credentials,
        INativeProviderHttpTransport transport)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options)))
            .Snapshot();
        _credentials = credentials
            ?? throw new ArgumentNullException(nameof(credentials));
        _transport = transport
            ?? throw new ArgumentNullException(nameof(transport));
        _endpoint = NativeEndpoint.Build(
            _options.BaseUri,
            _options.InteractionsPath,
            _options.AllowInsecureLoopback,
            nameof(options));
        _capabilities = new ProviderCapabilities
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            ReasoningInput = false,
            ParallelToolCalls = true,
            RequiresCompleteToolPairs = true,
            TextInput = true,
            StructuredInput = true,
            ReasoningEffort = _options.SupportsThinkingLevel,
            SamplingControls = _options.SupportsSamplingControls,
            Seed = _options.SupportsSeed,
            PromptCaching = false,
            StatefulContinuation = false,
            MaxTools = _options.MaxTools,
            MaxToolSchemaUtf8Bytes = NativeProviderLimits.MaxToolSchemaBytes,
            MaxContextTokens = _options.MaxContextTokens,
            MaxOutputTokens = _options.MaxOutputTokens
        };
        var dialect = new ProviderDialectContract(
            "gemini.interactions.sse.v1beta.v1",
            ProviderRequestFamily.Interactions,
            "gemini.interactions.request.v1beta.v1",
            ProviderStreamFraming.ServerSentEvents,
            "sse.typed-json.v1",
            "gemini.interactions.function-step.v1",
            "gemini.interactions.usage.v1",
            "gemini.interactions.thought-summary.v1",
            ContentType);
        _metadata = new ProviderRouteMetadata(
            _options.Model,
            dialect,
            PolicyVersion,
            NativeProviderRoute.PolicyDigest(
                ("model", _options.Model),
                ("endpoint", _endpoint.GetLeftPart(UriPartial.Authority)
                    + _endpoint.AbsolutePath),
                ("defaultThinkingLevel",
                    _options.DefaultThinkingLevel ?? "unspecified"),
                ("supportsThinkingLevel",
                    _options.SupportsThinkingLevel.ToString()),
                ("maxSseEventCharacters",
                    _options.MaxSseEventCharacters.ToString(
                        CultureInfo.InvariantCulture)),
                ("maxSseEvents", _options.MaxSseEvents.ToString(
                    CultureInfo.InvariantCulture)),
                ("maxSseLineCharacters",
                    _options.MaxSseLineCharacters.ToString(
                        CultureInfo.InvariantCulture)),
                ("maxStreamCharacters",
                    _options.MaxStreamCharacters.ToString(
                        CultureInfo.InvariantCulture)),
                ("thoughtSummaries", _options.IncludeThoughtSummaries.ToString()),
                ("toolChoice", _options.ToolChoice)));
    }

    public string ProviderId => _options.ProviderId;

    public ProviderCapabilities Capabilities => _capabilities.Clone();

    public ProviderRouteMetadata RouteMetadata => _metadata;

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        StreamingModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var route = new ProviderRouteIdentity(ProviderId, _metadata, _capabilities);
        var prepared = await PrepareStreamAsync(
                new ProviderStreamPreparationContext(ProviderId, route, request),
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await foreach (var item in prepared.StreamAsync(cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            await prepared.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask<PreparedProviderStream> PrepareStreamAsync(
        ProviderStreamPreparationContext context,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        NativeProviderRoute.Validate(context, ProviderId, _metadata);
        NativeProviderLimits.ValidateRequest(
            context.Request,
            _options.MaxTools,
            _options.MaxOutputTokens,
            cancellationToken);
        ValidateInference(context.Request.Inference);
        if (NativeProviderLimits.ContainsReasoning(context.Request))
        {
            throw Unsupported("reasoning-input replay");
        }

        if (context.Request.Tools.Count == 0
            && string.Equals(_options.ToolChoice, "required", StringComparison.Ordinal))
        {
            throw new ProviderException(
                "provider_tool_choice_requires_tools",
                "validation",
                "Required tool choice needs at least one tool definition.",
                false,
                usageKnownToBeZero: true);
        }

        byte[] body;
        try
        {
            body = Encode(context.Request);
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new ProviderException(
                "provider_request_encoding_failed",
                "validation",
                "The Interactions request could not be encoded.",
                false,
                innerException: exception,
                usageKnownToBeZero: true);
        }

        try
        {
            var evidence = ProviderWireRequestEvidence.CreateAvailable(
                body,
                ContentType,
                context.RouteIdentity);
            return new ValueTask<PreparedProviderStream>(
                new NativePreparedStream(
                    body,
                    evidence,
                    (payload, token) => StreamPreparedAsync(
                        context.Request.StreamAttemptId,
                        payload,
                        token)));
        }
        catch
        {
            Array.Clear(body, 0, body.Length);
            throw;
        }
    }

    private byte[] Encode(StreamingModelRequest request)
    {
        var inference = request.Inference?.CloneValidated();
        var system = request.Messages
            .Where(message => string.Equals(
                message.Role, NormalizedRoles.System, StringComparison.Ordinal))
            .SelectMany(message => message.Parts)
            .Where(IsTextPart)
            .Select(PartText)
            .Where(value => value.Length > 0)
            .ToArray();
        return NativeProviderJson.Encode(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("model", _options.Model);
            writer.WriteBoolean("stream", true);
            writer.WriteBoolean("store", false);
            if (system.Length > 0)
            {
                writer.WriteString("system_instruction", string.Join("\n", system));
            }

            writer.WritePropertyName("input");
            writer.WriteStartArray();
            foreach (var message in request.Messages)
            {
                if (!string.Equals(message.Role, NormalizedRoles.System, StringComparison.Ordinal))
                {
                    WriteSteps(writer, message);
                }
            }

            writer.WriteEndArray();
            if (request.Tools.Count > 0)
            {
                writer.WritePropertyName("tools");
                writer.WriteStartArray();
                foreach (var tool in request.Tools)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function");
                    writer.WriteString("name", tool.Name);
                    writer.WriteString("description", tool.Description);
                    writer.WritePropertyName("parameters");
                    tool.ParametersSchema.WriteTo(writer);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WritePropertyName("generation_config");
            writer.WriteStartObject();
            writer.WriteNumber(
                "max_output_tokens",
                request.MaxOutputTokens ?? _options.MaxOutputTokens);
            writer.WriteString("tool_choice", _options.ToolChoice);
            var thinking = ResolveThinkingLevel(inference);
            if (thinking is not null)
            {
                writer.WriteString("thinking_level", thinking);
            }

            writer.WriteString(
                "thinking_summaries",
                _options.IncludeThoughtSummaries ? "auto" : "none");
            if (inference?.Temperature is double temperature)
            {
                writer.WriteNumber("temperature", temperature);
            }

            if (inference?.TopP is double topP)
            {
                writer.WriteNumber("top_p", topP);
            }

            if (inference?.Seed is int seed)
            {
                writer.WriteNumber("seed", seed);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private static void WriteSteps(Utf8JsonWriter writer, NormalizedMessage message)
    {
        var text = message.Parts
            .Where(IsTextPart)
            .Select(PartText)
            .Where(value => value.Length > 0)
            .ToArray();
        if (text.Length > 0)
        {
            writer.WriteStartObject();
            writer.WriteString(
                "type",
                string.Equals(message.Role, NormalizedRoles.Assistant, StringComparison.Ordinal)
                    ? "model_output"
                    : "user_input");
            writer.WriteString("content", string.Join("\n", text));
            writer.WriteEndObject();
        }

        foreach (var part in message.Parts)
        {
            if (string.Equals(part.Type, NormalizedPartTypes.ToolCall, StringComparison.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "function_call");
                writer.WriteString("id", part.ToolCallId);
                writer.WriteString("name", part.ToolName);
                writer.WritePropertyName("arguments");
                if (part.Json.HasValue)
                {
                    part.Json.Value.WriteTo(writer);
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }
            else if (string.Equals(part.Type, NormalizedPartTypes.ToolResult, StringComparison.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "function_result");
                writer.WriteString("call_id", part.ToolCallId);
                writer.WriteString("name", part.ToolName);
                writer.WritePropertyName("result");
                if (part.Json.HasValue)
                {
                    part.Json.Value.WriteTo(writer);
                }
                else
                {
                    writer.WriteNullValue();
                }

                writer.WriteEndObject();
            }
        }
    }

    private static bool IsTextPart(NormalizedContentPart part) =>
        string.Equals(part.Type, NormalizedPartTypes.Text, StringComparison.Ordinal)
        || string.Equals(part.Type, NormalizedPartTypes.Json, StringComparison.Ordinal);

    private static string PartText(NormalizedContentPart part) =>
        string.Equals(part.Type, NormalizedPartTypes.Json, StringComparison.Ordinal)
            ? part.Json?.GetRawText() ?? string.Empty
            : part.Text ?? string.Empty;

    private string? ResolveThinkingLevel(ModelInferenceOptions? inference)
    {
        if (inference?.ReasoningEnabled == false
            || string.Equals(
                inference?.ReasoningEffort,
                ModelReasoningEfforts.None,
                StringComparison.Ordinal))
        {
            return "minimal";
        }

        var effort = inference?.ReasoningEffort;
        if (effort is not null)
        {
            return effort switch
            {
                ModelReasoningEfforts.ExtraHigh or ModelReasoningEfforts.Maximum => "high",
                _ => effort
            };
        }

        return inference?.ReasoningEnabled == true
            ? _options.DefaultThinkingLevel ?? "medium"
            : _options.DefaultThinkingLevel;
    }

    private void ValidateInference(ModelInferenceOptions? inference)
    {
        var value = inference?.CloneValidated();
        if (value is null)
        {
            return;
        }

        if (value.ReasoningTokenBudget.HasValue)
        {
            throw Unsupported("reasoning token budget");
        }

        if ((value.ReasoningEnabled.HasValue
             || value.ReasoningEffort is not null)
            && !_options.SupportsThinkingLevel)
        {
            throw Unsupported("reasoning control");
        }

        if ((value.Temperature.HasValue || value.TopP.HasValue)
            && !_options.SupportsSamplingControls)
        {
            throw Unsupported("sampling control");
        }

        if (value.Seed.HasValue && !_options.SupportsSeed)
        {
            throw Unsupported("seed");
        }

        if (value.PromptCachingEnabled.HasValue
            || value.PromptCacheKey is not null
            || value.PromptCacheRetention is not null)
        {
            throw Unsupported("prompt-cache control");
        }
    }

    private static ProviderException Unsupported(string control) => new(
        "provider_inference_control_unsupported",
        "capability",
        $"The selected provider route does not support {control}.",
        ProviderFailureDisposition.Failover,
        usageKnownToBeZero: true);

    private async IAsyncEnumerable<ModelStreamEvent> StreamPreparedAsync(
        string streamAttemptId,
        byte[] body,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string credential;
        try
        {
            credential = NativeCredential.Validate(
                await _credentials.GetCredentialAsync(cancellationToken)
                    .ConfigureAwait(false),
                "credential");
        }
        catch (OperationCanceledException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw NativeProviderErrors.MissingCredential(exception);
        }
        catch (Exception exception)
        {
            throw NativeProviderErrors.MissingCredential(exception);
        }

        INativeProviderHttpResponse response;
        try
        {
            response = await _transport.SendAsync(
                    new NativeProviderHttpRequest
                    {
                        Uri = _endpoint,
                        CredentialHeaderName = "x-goog-api-key",
                        CredentialHeaderValue = credential,
                        Body = body,
                        ContentType = ContentType
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw NativeProviderErrors.Connect(exception);
        }
        catch (Exception exception)
        {
            throw NativeProviderErrors.Connect(exception);
        }
        finally
        {
            credential = string.Empty;
            Array.Clear(body, 0, body.Length);
        }

        using (response)
        {
            if (response.StatusCode is < 200 or >= 300)
            {
                throw NativeProviderErrors.Http(
                    response.StatusCode,
                    response.GetHeader("Retry-After"));
            }

            using var registration = cancellationToken.Register(response.Dispose);
            var parser = new InteractionsParser(streamAttemptId);
            var eventCount = 0;
            await foreach (var record in NativeSseReader.ReadAsync(
                               response.Content,
                               _options.MaxSseLineCharacters,
                               _options.MaxSseEventCharacters,
                               _options.MaxStreamCharacters,
                               cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (++eventCount > _options.MaxSseEvents)
                {
                    throw new ProviderException(
                        "provider_sse_event_limit",
                        "provider",
                        "The provider emitted too many SSE events.",
                        false);
                }

                if (string.Equals(record.Data, "[DONE]", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var item in parser.Parse(record))
                {
                    yield return item;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!parser.IsComplete)
            {
                throw new ProviderException(
                    "provider_stream_terminal_missing",
                    "network",
                    "The provider stream ended before its terminal event.",
                    true);
            }
        }
    }

    private sealed class InteractionsParser
    {
        private readonly string _streamAttemptId;
        private readonly Dictionary<int, ToolState> _tools = new();
        private long _ordinal;

        internal InteractionsParser(string streamAttemptId)
        {
            _streamAttemptId = streamAttemptId;
        }

        internal bool IsComplete { get; private set; }

        internal IReadOnlyList<ModelStreamEvent> Parse(NativeSseRecord record)
        {
            if (IsComplete)
            {
                throw NativeProviderJson.ProtocolError(
                    "The provider emitted data after completion.");
            }

            var payload = NativeProviderJson.RequireObject(record.Data);
            NativeProviderJson.TryString(payload, "type", out var payloadType);
            if (payloadType is not null
                && record.EventName is not null
                && !string.Equals(
                    payloadType,
                    record.EventName,
                    StringComparison.Ordinal))
            {
                throw NativeProviderJson.ProtocolError(
                    "The provider event type is inconsistent.");
            }

            var type = payloadType ?? record.EventName;
            if (string.IsNullOrWhiteSpace(type))
            {
                throw NativeProviderJson.ProtocolError(
                    "The provider event type is missing.");
            }

            var events = new List<ModelStreamEvent>();
            switch (type)
            {
                case "step.start":
                    ParseStepStart(payload, events);
                    break;
                case "step.delta":
                    ParseStepDelta(payload, events);
                    break;
                case "step.stop":
                    ParseStepStop(payload, events);
                    break;
                case "interaction.completed":
                case "interaction.complete":
                    ParseCompleted(payload, events);
                    break;
                case "interaction.failed":
                case "error":
                    throw new ProviderException(
                        "provider_stream_error",
                        "provider",
                        "The provider reported a streaming error.",
                        false);
            }

            return events;
        }

        private void ParseStepStart(
            JsonElement payload,
            ICollection<ModelStreamEvent> events)
        {
            var step = payload.TryGetProperty("step", out var nested)
                ? nested
                : payload;
            if (!NativeProviderJson.TryString(step, "type", out var stepType)
                || !string.Equals(stepType, "function_call", StringComparison.Ordinal))
            {
                return;
            }

            var index = ReadIndex(payload);
            if (!TryAnyString(step, out var id, "id", "call_id")
                || !NativeProviderJson.TryString(step, "name", out var name)
                || !_tools.TryAdd(index, new ToolState(id!, name!)))
            {
                throw NativeProviderJson.ProtocolError(
                    "The provider emitted an invalid function step.");
            }

            events.Add(Event(
                ModelStreamEventKinds.ToolCallDelta,
                toolCallId: id,
                toolName: name));
        }

        private void ParseStepDelta(
            JsonElement payload,
            ICollection<ModelStreamEvent> events)
        {
            var delta = payload.TryGetProperty("delta", out var nested)
                ? nested
                : payload;
            if (!NativeProviderJson.TryString(delta, "type", out var deltaType))
            {
                throw NativeProviderJson.ProtocolError(
                    "The provider emitted an invalid step delta.");
            }

            if (string.Equals(deltaType, "text", StringComparison.Ordinal)
                || string.Equals(deltaType, "text_delta", StringComparison.Ordinal))
            {
                if (!TryAnyString(delta, out var text, "text", "delta"))
                {
                    throw NativeProviderJson.ProtocolError(
                        "The provider emitted an invalid text delta.");
                }

                if (text!.Length > 0)
                {
                    events.Add(Event(ModelStreamEventKinds.TextDelta, text: text));
                }
            }
            else if (string.Equals(deltaType, "thought", StringComparison.Ordinal)
                     || string.Equals(deltaType, "thought_summary", StringComparison.Ordinal))
            {
                if (!TryAnyString(delta, out var reasoning, "text", "delta"))
                {
                    throw NativeProviderJson.ProtocolError(
                        "The provider emitted an invalid thought delta.");
                }

                if (reasoning!.Length > 0)
                {
                    events.Add(Event(
                        ModelStreamEventKinds.ReasoningDelta,
                        reasoning: reasoning));
                }
            }
            else if (string.Equals(deltaType, "arguments", StringComparison.Ordinal)
                     || string.Equals(deltaType, "arguments_delta", StringComparison.Ordinal)
                     || string.Equals(deltaType, "function_call_arguments", StringComparison.Ordinal))
            {
                var index = ReadIndex(payload);
                if (!_tools.TryGetValue(index, out var tool)
                    || !TryAnyString(
                        delta,
                        out var arguments,
                        "arguments_delta",
                        "arguments",
                        "text",
                        "delta"))
                {
                    throw NativeProviderJson.ProtocolError(
                        "The provider emitted an invalid function delta.");
                }

                if (arguments!.Length > 0)
                {
                    tool.HasArgumentDelta = true;
                    events.Add(Event(
                        ModelStreamEventKinds.ToolCallDelta,
                        toolCallId: tool.Id,
                        arguments: arguments));
                }
            }
        }

        private void ParseStepStop(
            JsonElement payload,
            ICollection<ModelStreamEvent> events)
        {
            var index = ReadIndex(payload);
            if (!_tools.TryGetValue(index, out var tool)
                || tool.HasArgumentDelta)
            {
                return;
            }

            var step = payload.TryGetProperty("step", out var nested)
                ? nested
                : payload;
            if (!step.TryGetProperty("arguments", out var arguments))
            {
                return;
            }

            var json = arguments.ValueKind == JsonValueKind.String
                ? arguments.GetString()
                : arguments.GetRawText();
            if (!string.IsNullOrEmpty(json))
            {
                tool.HasArgumentDelta = true;
                events.Add(Event(
                    ModelStreamEventKinds.ToolCallDelta,
                    toolCallId: tool.Id,
                    arguments: json));
            }
        }

        private void ParseCompleted(
            JsonElement payload,
            ICollection<ModelStreamEvent> events)
        {
            var interaction = payload.TryGetProperty("interaction", out var nested)
                ? nested
                : payload;
            if ((!interaction.TryGetProperty("usage", out var usageValue)
                 || usageValue.ValueKind != JsonValueKind.Object)
                && (!interaction.TryGetProperty(
                        "usage_metadata",
                        out usageValue)
                    || usageValue.ValueKind != JsonValueKind.Object))
            {
                throw NativeProviderJson.ProtocolError(
                    "The provider terminal event omitted token usage.");
            }

            var usage = ParseUsage(usageValue);
            events.Add(Event(ModelStreamEventKinds.Usage, usage: usage));
            NativeProviderJson.TryString(interaction, "status", out var status);
            var finish = _tools.Count > 0
                         || string.Equals(status, "requires_action", StringComparison.Ordinal)
                ? "tool_calls"
                : string.Equals(status, "incomplete", StringComparison.Ordinal)
                    ? "length"
                    : "stop";
            events.Add(Event(ModelStreamEventKinds.Completed, finishReason: finish));
            IsComplete = true;
        }

        private static ProviderUsage ParseUsage(JsonElement value)
        {
            if (!TryAnyInt(
                    value,
                    out var input,
                    "total_input_tokens",
                    "input_tokens",
                    "prompt_token_count")
                || !TryAnyInt(
                    value,
                    out var output,
                    "total_output_tokens",
                    "output_tokens",
                    "candidates_token_count"))
            {
                throw NativeProviderJson.ProtocolError(
                    "The provider emitted incomplete token usage.");
            }

            var cachedReported = TryAnyInt(
                value,
                out var cachedValue,
                "total_cached_tokens",
                "cached_tokens",
                "cached_content_token_count");
            var reasoningReported = TryAnyInt(
                value,
                out var reasoningValue,
                "total_thought_tokens",
                "thought_tokens",
                "thoughts_token_count");
            var totalReported = TryAnyInt(
                value,
                out var total,
                "total_tokens",
                "total_token_count");
            var cached = cachedReported ? cachedValue : (int?)null;
            return new ProviderUsage
            {
                InputTokens = input,
                OutputTokens = output,
                CacheReadTokens = cached,
                CacheMissTokens = cached.HasValue && cached.Value <= input
                    ? input - cached.Value
                    : null,
                ReasoningTokens = reasoningReported ? reasoningValue : null,
                ProviderTotalTokens = totalReported ? total : input + output,
                CostUsd = "0",
                Availability = UsageAvailabilityStates.CostUnavailable
            };
        }

        private static int ReadIndex(JsonElement value)
        {
            if (TryAnyInt(value, out var index, "step_index", "index", "output_index"))
            {
                return index;
            }

            throw NativeProviderJson.ProtocolError(
                "The provider step index is missing.");
        }

        private static bool TryAnyString(
            JsonElement value,
            out string? result,
            params string[] names)
        {
            foreach (var name in names)
            {
                if (NativeProviderJson.TryString(value, name, out result))
                {
                    return true;
                }
            }

            result = null;
            return false;
        }

        private static bool TryAnyInt(
            JsonElement value,
            out int result,
            params string[] names)
        {
            foreach (var name in names)
            {
                if (NativeProviderJson.TryInt(value, name, out result))
                {
                    return true;
                }
            }

            result = 0;
            return false;
        }

        private ModelStreamEvent Event(
            string kind,
            string? text = null,
            string? reasoning = null,
            string? toolCallId = null,
            string? toolName = null,
            string? arguments = null,
            ProviderUsage? usage = null,
            string? finishReason = null) => new()
            {
                StreamAttemptId = _streamAttemptId,
                Ordinal = _ordinal++,
                Kind = kind,
                TextDelta = text,
                ReasoningDelta = reasoning,
                ToolCallId = toolCallId,
                ToolNameDelta = toolName,
                ArgumentsJsonDelta = arguments,
                Usage = usage,
                FinishReason = finishReason
            };

        private sealed class ToolState
        {
            internal ToolState(string id, string name)
            {
                Id = id;
                Name = name;
            }

            internal string Id { get; }

            internal string Name { get; }

            internal bool HasArgumentDelta { get; set; }
        }
    }
}
