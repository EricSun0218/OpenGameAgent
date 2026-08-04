using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Providers.Native;

public sealed class OpenAiResponsesStreamingProvider :
    IStreamingModelProvider,
    IPreparedStreamingModelProvider,
    IProviderRouteMetadataSource
{
    private const string ContentType = "application/json; charset=utf-8";
    private const string PolicyVersion = "openai-responses.route-policy.v1";

    private readonly OpenAiResponsesProviderOptions _options;
    private readonly INativeApiCredentialSource _credentials;
    private readonly INativeProviderHttpTransport _transport;
    private readonly ProviderCapabilities _capabilities;
    private readonly ProviderRouteMetadata _metadata;
    private readonly Uri _endpoint;

    public OpenAiResponsesStreamingProvider(
        OpenAiResponsesProviderOptions options,
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
            _options.ResponsesPath,
            _options.AllowInsecureLoopback,
            nameof(options));
        _capabilities = new ProviderCapabilities
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            ReasoningInput = false,
            ParallelToolCalls = _options.ParallelToolCalls,
            RequiresCompleteToolPairs = true,
            TextInput = true,
            StructuredInput = true,
            ReasoningEffort = _options.SupportsReasoningEffort,
            SamplingControls = _options.SupportsSamplingControls,
            Seed = _options.SupportsSeed,
            PromptCaching = true,
            AutomaticPromptCaching = true,
            PromptCacheKey = _options.SupportsPromptCacheKey,
            StatefulContinuation = false,
            MaxTools = _options.MaxTools,
            MaxToolSchemaUtf8Bytes = NativeProviderLimits.MaxToolSchemaBytes,
            MaxContextTokens = _options.MaxContextTokens,
            MaxOutputTokens = _options.MaxOutputTokens
        };
        var dialect = new ProviderDialectContract(
            "openai.responses.sse.v1",
            ProviderRequestFamily.Responses,
            "openai.responses.request.v1",
            ProviderStreamFraming.ServerSentEvents,
            "sse.typed-json.v1",
            "openai.responses.function-call.v1",
            "openai.responses.usage.v1",
            "openai.responses.reasoning-summary.v1",
            ContentType);
        _metadata = new ProviderRouteMetadata(
            _options.Model,
            dialect,
            PolicyVersion,
            NativeProviderRoute.PolicyDigest(
                ("model", _options.Model),
                ("endpoint", _endpoint.GetLeftPart(UriPartial.Authority)
                    + _endpoint.AbsolutePath),
                ("defaultReasoningEffort",
                    _options.DefaultReasoningEffort ?? "unspecified"),
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
                ("parallelTools", _options.ParallelToolCalls.ToString()),
                ("strictTools", _options.StrictToolSchemas.ToString()),
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
                "The Responses request could not be encoded.",
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
        return NativeProviderJson.Encode(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("model", _options.Model);
            writer.WriteBoolean("stream", true);
            writer.WriteBoolean("store", false);
            writer.WriteNumber(
                "max_output_tokens",
                request.MaxOutputTokens ?? _options.MaxOutputTokens);

            var effort = ResolveReasoningEffort(inference);
            if (effort is not null)
            {
                writer.WritePropertyName("reasoning");
                writer.WriteStartObject();
                writer.WriteString("effort", effort);
                writer.WriteString("summary", "auto");
                writer.WriteEndObject();
            }

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

            if (inference?.PromptCacheKey is string cacheKey)
            {
                writer.WriteString("prompt_cache_key", cacheKey);
            }

            writer.WritePropertyName("input");
            writer.WriteStartArray();
            foreach (var message in request.Messages)
            {
                WriteMessageItems(writer, message);
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
                    writer.WriteBoolean("strict", _options.StrictToolSchemas);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteString("tool_choice", _options.ToolChoice);
                writer.WriteBoolean(
                    "parallel_tool_calls",
                    _options.ParallelToolCalls);
            }

            writer.WriteEndObject();
        });
    }

    private static void WriteMessageItems(
        Utf8JsonWriter writer,
        NormalizedMessage message)
    {
        var content = message.Parts.Where(part =>
            string.Equals(part.Type, NormalizedPartTypes.Text, StringComparison.Ordinal)
            || string.Equals(part.Type, NormalizedPartTypes.Json, StringComparison.Ordinal)).ToArray();
        if (content.Length > 0)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "message");
            writer.WriteString("role", message.Role);
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            foreach (var part in content)
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "type",
                    string.Equals(message.Role, NormalizedRoles.Assistant, StringComparison.Ordinal)
                        ? "output_text"
                        : "input_text");
                writer.WritePropertyName("text");
                NativeProviderJson.WriteContentText(writer, part);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        foreach (var part in message.Parts)
        {
            if (string.Equals(part.Type, NormalizedPartTypes.ToolCall, StringComparison.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "function_call");
                writer.WriteString("call_id", part.ToolCallId);
                writer.WriteString("name", part.ToolName);
                writer.WriteString(
                    "arguments",
                    part.Json?.GetRawText() ?? "{}");
                writer.WriteEndObject();
            }
            else if (string.Equals(part.Type, NormalizedPartTypes.ToolResult, StringComparison.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "function_call_output");
                writer.WriteString("call_id", part.ToolCallId);
                writer.WriteString("output", part.Json?.GetRawText() ?? "null");
                writer.WriteEndObject();
            }
        }
    }

    private string? ResolveReasoningEffort(ModelInferenceOptions? inference)
    {
        if (inference?.ReasoningEnabled == false
            || string.Equals(
                inference?.ReasoningEffort,
                ModelReasoningEfforts.None,
                StringComparison.Ordinal))
        {
            return "none";
        }

        var effort = inference?.ReasoningEffort ??
                     (inference?.ReasoningEnabled == true
                         ? _options.DefaultReasoningEffort ?? "medium"
                         : _options.DefaultReasoningEffort);
        return string.Equals(
            effort,
            ModelReasoningEfforts.Maximum,
            StringComparison.Ordinal)
            ? ModelReasoningEfforts.ExtraHigh
            : effort;
    }

    private void ValidateInference(ModelInferenceOptions? inference)
    {
        var value = inference?.CloneValidated();
        if (value is null)
        {
            return;
        }

        if ((value.ReasoningEnabled.HasValue
             || value.ReasoningEffort is not null)
            && !_options.SupportsReasoningEffort)
        {
            throw Unsupported("reasoning control");
        }

        if (value.ReasoningTokenBudget.HasValue)
        {
            throw Unsupported("reasoning token budget");
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

        if (value.PromptCachingEnabled == false
            || value.PromptCacheRetention is not null
            || value.PromptCacheKey is not null
               && !_options.SupportsPromptCacheKey)
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
                        CredentialHeaderName = "Authorization",
                        CredentialHeaderValue = "Bearer " + credential,
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
            var parser = new ResponsesParser(streamAttemptId);
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

    private sealed class ResponsesParser
    {
        private readonly string _streamAttemptId;
        private readonly Dictionary<int, ToolState> _tools = new();
        private long _ordinal;

        internal ResponsesParser(string streamAttemptId)
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
            var type = payloadType ?? record.EventName;
            if (string.IsNullOrWhiteSpace(type)
                || record.EventName is not null
                   && payloadType is not null
                   && !string.Equals(record.EventName, payloadType, StringComparison.Ordinal))
            {
                throw NativeProviderJson.ProtocolError(
                    "The provider emitted an inconsistent event type.");
            }

            var events = new List<ModelStreamEvent>();
            switch (type)
            {
                case "response.output_text.delta":
                case "response.refusal.delta":
                    AddStringDelta(payload, "delta", ModelStreamEventKinds.TextDelta, events);
                    break;
                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                    AddStringDelta(payload, "delta", ModelStreamEventKinds.ReasoningDelta, events);
                    break;
                case "response.output_item.added":
                    ParseItemAdded(payload, events);
                    break;
                case "response.function_call_arguments.delta":
                    ParseArgumentsDelta(payload, events);
                    break;
                case "response.function_call_arguments.done":
                    ParseArgumentsDone(payload, events);
                    break;
                case "response.output_item.done":
                    ParseItemDone(payload, events);
                    break;
                case "response.completed":
                case "response.incomplete":
                    ParseCompleted(payload, type, events);
                    break;
                case "error":
                case "response.failed":
                    throw new ProviderException(
                        "provider_stream_error",
                        "provider",
                        "The provider reported a streaming error.",
                        false);
            }

            return events;
        }

        private void AddStringDelta(
            JsonElement payload,
            string propertyName,
            string kind,
            ICollection<ModelStreamEvent> events)
        {
            if (!NativeProviderJson.TryString(payload, propertyName, out var value))
            {
                throw NativeProviderJson.ProtocolError(
                    "The provider emitted an invalid text delta.");
            }

            if (value!.Length > 0)
            {
                events.Add(Event(
                    kind,
                    text: kind == ModelStreamEventKinds.TextDelta ? value : null,
                    reasoning: kind == ModelStreamEventKinds.ReasoningDelta ? value : null));
            }
        }

        private void ParseItemAdded(
            JsonElement payload,
            ICollection<ModelStreamEvent> events)
        {
            if (!TryIndex(payload, out var index)
                || !payload.TryGetProperty("item", out var item)
                || !NativeProviderJson.TryString(item, "type", out var itemType)
                || !string.Equals(itemType, "function_call", StringComparison.Ordinal))
            {
                return;
            }

            if (!NativeProviderJson.TryString(item, "call_id", out var id)
                || !NativeProviderJson.TryString(item, "name", out var name)
                || !_tools.TryAdd(index, new ToolState(id!, name!)))
            {
                throw NativeProviderJson.ProtocolError(
                    "The provider emitted an invalid function call.");
            }

            events.Add(Event(
                ModelStreamEventKinds.ToolCallDelta,
                toolCallId: id,
                toolName: name));
        }

        private void ParseArgumentsDelta(
            JsonElement payload,
            ICollection<ModelStreamEvent> events)
        {
            if (!TryIndex(payload, out var index)
                || !_tools.TryGetValue(index, out var tool)
                || !NativeProviderJson.TryString(payload, "delta", out var delta))
            {
                throw NativeProviderJson.ProtocolError(
                    "The provider emitted an invalid function-call delta.");
            }

            if (delta!.Length > 0)
            {
                tool.HasArgumentDelta = true;
                events.Add(Event(
                    ModelStreamEventKinds.ToolCallDelta,
                    toolCallId: tool.Id,
                    arguments: delta));
            }
        }

        private void ParseArgumentsDone(
            JsonElement payload,
            ICollection<ModelStreamEvent> events)
        {
            if (!TryIndex(payload, out var index)
                || !_tools.TryGetValue(index, out var tool))
            {
                throw NativeProviderJson.ProtocolError(
                    "The provider completed an unknown function call.");
            }

            if (!tool.HasArgumentDelta
                && NativeProviderJson.TryString(payload, "arguments", out var arguments)
                && arguments!.Length > 0)
            {
                tool.HasArgumentDelta = true;
                events.Add(Event(
                    ModelStreamEventKinds.ToolCallDelta,
                    toolCallId: tool.Id,
                    arguments: arguments));
            }
        }

        private void ParseItemDone(
            JsonElement payload,
            ICollection<ModelStreamEvent> events)
        {
            if (!TryIndex(payload, out var index)
                || !_tools.TryGetValue(index, out var tool)
                || tool.HasArgumentDelta
                || !payload.TryGetProperty("item", out var item)
                || !NativeProviderJson.TryString(item, "arguments", out var arguments)
                || arguments!.Length == 0)
            {
                return;
            }

            tool.HasArgumentDelta = true;
            events.Add(Event(
                ModelStreamEventKinds.ToolCallDelta,
                toolCallId: tool.Id,
                arguments: arguments));
        }

        private void ParseCompleted(
            JsonElement payload,
            string type,
            ICollection<ModelStreamEvent> events)
        {
            var response = payload.TryGetProperty("response", out var nested)
                ? nested
                : payload;
            if (!response.TryGetProperty("usage", out var usageValue)
                || usageValue.ValueKind != JsonValueKind.Object)
            {
                throw NativeProviderJson.ProtocolError(
                    "The provider terminal event omitted token usage.");
            }

            var usage = ParseUsage(usageValue);
            events.Add(Event(ModelStreamEventKinds.Usage, usage: usage));
            var finishReason = _tools.Count > 0
                ? "tool_calls"
                : string.Equals(type, "response.incomplete", StringComparison.Ordinal)
                  || NativeProviderJson.TryString(response, "status", out var status)
                     && string.Equals(status, "incomplete", StringComparison.Ordinal)
                    ? "length"
                    : "stop";
            events.Add(Event(
                ModelStreamEventKinds.Completed,
                finishReason: finishReason));
            IsComplete = true;
        }

        private static ProviderUsage ParseUsage(JsonElement value)
        {
            if (!NativeProviderJson.TryInt(
                    value,
                    "input_tokens",
                    out var input)
                || !NativeProviderJson.TryInt(
                    value,
                    "output_tokens",
                    out var output))
            {
                throw NativeProviderJson.ProtocolError(
                    "The provider emitted incomplete token usage.");
            }

            int? cached = null;
            int? reasoning = null;
            if (value.TryGetProperty("input_tokens_details", out var inputDetails)
                && NativeProviderJson.TryInt(inputDetails, "cached_tokens", out var cachedValue))
            {
                cached = cachedValue;
            }

            if (value.TryGetProperty("output_tokens_details", out var outputDetails)
                && NativeProviderJson.TryInt(outputDetails, "reasoning_tokens", out var reasoningValue))
            {
                reasoning = reasoningValue;
            }

            var totalReported = NativeProviderJson.TryInt(
                value,
                "total_tokens",
                out var total);
            return new ProviderUsage
            {
                InputTokens = input,
                OutputTokens = output,
                CacheReadTokens = cached,
                CacheMissTokens = cached.HasValue && cached.Value <= input
                    ? input - cached.Value
                    : null,
                ReasoningTokens = reasoning,
                ProviderTotalTokens = totalReported ? total : input + output,
                CostUsd = "0",
                Availability = UsageAvailabilityStates.CostUnavailable
            };
        }

        private static bool TryIndex(JsonElement payload, out int index) =>
            NativeProviderJson.TryInt(payload, "output_index", out index)
            || NativeProviderJson.TryInt(payload, "item_index", out index);

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
