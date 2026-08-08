using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Providers.OpenAI;

internal sealed class ResponsesStreamState
{
    private readonly string _requestModel;
    private readonly string _providerId;
    private readonly string _apiId;
    private readonly IReadOnlyDictionary<string, string> _grammarInputProperties;
    private readonly int _maximumCharacters;
    private readonly int _maximumToolCalls;
    private readonly SortedDictionary<int, OutputSlot> _slots = new();
    private long _characters;
    private bool _terminal;
    private string? _responseId;
    private string? _responseModel;
    private string? _rawStopReason;
    private ModelStopReason _stopReason = ModelStopReason.Pending;
    private string? _errorMessage;
    private ModelUsage _usage = new();

    public ResponsesStreamState(
        string requestModel,
        string providerId,
        string apiId,
        IReadOnlyDictionary<string, string> grammarInputProperties,
        int maximumCharacters,
        int maximumToolCalls)
    {
        _requestModel = requestModel;
        _providerId = providerId;
        _apiId = apiId;
        _grammarInputProperties = grammarInputProperties;
        _maximumCharacters = maximumCharacters;
        _maximumToolCalls = maximumToolCalls;
    }

    public IReadOnlyList<ModelStreamEvent> Apply(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
            var root = document.RootElement;
            RequireKind(root, JsonValueKind.Object, "A Responses stream event must be a JSON object.");
            EnsureUnambiguous(root);
            var type = RequiredString(root, "type");
            var updates = new List<ModelStreamEvent>();
            switch (type)
            {
                case "response.created":
                    if (root.TryGetProperty("response", out var created))
                    {
                        ReadResponseIdentity(created);
                    }

                    break;
                case "response.output_item.added":
                    CreateSlot(RequiredIndex(root), RequiredObject(root, "item"), updates);
                    break;
                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                    ApplyTextDelta(root, SlotKind.Reasoning, ModelStreamEventKind.ReasoningDelta, updates);
                    break;
                case "response.reasoning_summary_part.done":
                    ApplyLiteralDelta(root, SlotKind.Reasoning, "\n\n", ModelStreamEventKind.ReasoningDelta, updates);
                    break;
                case "response.output_text.delta":
                case "response.refusal.delta":
                    ApplyTextDelta(root, SlotKind.Text, ModelStreamEventKind.TextDelta, updates);
                    break;
                case "response.function_call_arguments.delta":
                    ApplyFunctionArgumentsDelta(root, updates);
                    break;
                case "response.function_call_arguments.done":
                    ApplyFunctionArgumentsDone(root, updates);
                    break;
                case "response.custom_tool_call_input.delta":
                    ApplyCustomInputDelta(root, updates);
                    break;
                case "response.custom_tool_call_input.done":
                    ApplyCustomInputDone(root, updates);
                    break;
                case "response.output_item.done":
                    CompleteSlot(RequiredIndex(root), RequiredObject(root, "item"), updates);
                    break;
                case "response.completed":
                case "response.incomplete":
                    CompleteResponse(RequiredObject(root, "response"));
                    break;
                case "response.failed":
                    _terminal = true;
                    var failed = RequiredObject(root, "response");
                    ReadResponseIdentity(failed);
                    _rawStopReason = OptionalString(failed, "status") ?? "failed";
                    throw new InvalidDataException(ReadFailure(failed));
                case "error":
                    throw new InvalidDataException(
                        $"Responses stream error {OptionalString(root, "code") ?? "unknown"}: "
                        + (OptionalString(root, "message") ?? "No message was supplied."));
            }

            return updates;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Responses stream contained invalid JSON.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("The Responses stream did not match the expected response shape.", exception);
        }
    }

    public ModelResponse Partial() => BuildResponse(ModelStopReason.Pending, null);

    public ModelResponse Complete()
    {
        if (!_terminal || _stopReason == ModelStopReason.Pending)
        {
            throw new InvalidDataException("The Responses stream ended before a terminal response event.");
        }

        if (_slots.Values.Any(slot => !slot.Ended))
        {
            throw new InvalidDataException("The Responses stream ended with incomplete output items.");
        }

        return BuildResponse(_stopReason, _errorMessage);
    }

    private ModelResponse BuildResponse(ModelStopReason reason, string? errorMessage)
    {
        var content = new List<AgentContent>();
        foreach (var slot in _slots.Values)
        {
            switch (slot.Kind)
            {
                case SlotKind.Reasoning:
                    content.Add(new ReasoningContent(slot.Buffer.ToString(), slot.Signature));
                    break;
                case SlotKind.Text:
                    content.Add(new TextContent(slot.Buffer.ToString(), slot.Signature, ParsePhase(slot.Phase)));
                    break;
                case SlotKind.FunctionTool:
                case SlotKind.CustomTool:
                    if (!slot.Ended && reason == ModelStopReason.Pending)
                    {
                        break;
                    }

                    var arguments = slot.Buffer.Length == 0 ? "{}" : slot.Buffer.ToString();
                    if (!IsJsonObject(arguments))
                    {
                        if (reason == ModelStopReason.Length)
                        {
                            arguments = "{}";
                        }
                        else
                        {
                            throw new InvalidDataException("A completed Responses tool call did not contain a JSON object.");
                        }
                    }

                    content.Add(new ToolCallContent(
                        slot.CallId + "|" + slot.ItemId,
                        slot.Name!,
                        arguments,
                        slot.ThoughtSignature,
                        slot.Namespace));
                    break;
            }
        }

        return new ModelResponse(
            content,
            reason,
            _usage,
            errorMessage,
            _providerId,
            _apiId,
            _responseModel ?? _requestModel,
            _responseId,
            _rawStopReason,
            endTurn: _slots.Values.Any(slot => slot.Phase == "final_answer") ? true : null);
    }

    private void CreateSlot(int outputIndex, JsonElement item, ICollection<ModelStreamEvent> updates)
    {
        if (_terminal)
        {
            throw new InvalidDataException("The Responses stream emitted output after its terminal event.");
        }

        if (_slots.ContainsKey(outputIndex))
        {
            throw new InvalidDataException("The Responses stream reused an output index.");
        }

        var type = RequiredString(item, "type");
        OutputSlot? slot = type switch
        {
            "reasoning" => new OutputSlot(outputIndex, SlotKind.Reasoning),
            "message" => new OutputSlot(outputIndex, SlotKind.Text),
            "function_call" => CreateToolSlot(outputIndex, item, custom: false),
            "custom_tool_call" => CreateToolSlot(outputIndex, item, custom: true),
            _ => null,
        };
        if (slot is null)
        {
            return;
        }

        if (slot.Kind is SlotKind.FunctionTool or SlotKind.CustomTool
            && _slots.Values.Count(value => value.Kind is SlotKind.FunctionTool or SlotKind.CustomTool) >= _maximumToolCalls)
        {
            throw new InvalidDataException("The Responses output exceeded the configured tool-call limit.");
        }

        _slots.Add(outputIndex, slot);
        var kind = slot.Kind switch
        {
            SlotKind.Reasoning => ModelStreamEventKind.ReasoningStarted,
            SlotKind.Text => ModelStreamEventKind.TextStarted,
            _ => ModelStreamEventKind.ToolCallStarted,
        };
        updates.Add(ModelStreamEvent.Update(
            kind,
            Partial(),
            contentIndex: ContentIndex(outputIndex),
            toolCallId: slot.CallId,
            toolName: slot.Name));
    }

    private OutputSlot CreateToolSlot(int outputIndex, JsonElement item, bool custom)
    {
        var name = RequiredString(item, "name");
        var slot = new OutputSlot(outputIndex, custom ? SlotKind.CustomTool : SlotKind.FunctionTool)
        {
            CallId = RequiredString(item, "call_id"),
            ItemId = RequiredString(item, "id"),
            Name = name,
            Namespace = OptionalString(item, "namespace"),
        };
        if (custom)
        {
            slot.CustomProperty = _grammarInputProperties.TryGetValue(name, out var property) ? property : "input";
            slot.CustomInput.Append(OptionalString(item, "input") ?? string.Empty);
        }
        else
        {
            slot.Buffer.Append(OptionalString(item, "arguments") ?? string.Empty);
        }

        return slot;
    }

    private void CompleteSlot(int outputIndex, JsonElement item, ICollection<ModelStreamEvent> updates)
    {
        if (!_slots.TryGetValue(outputIndex, out var slot))
        {
            CreateSlot(outputIndex, item, updates);
            if (!_slots.TryGetValue(outputIndex, out slot))
            {
                return;
            }
        }

        if (slot.Ended)
        {
            throw new InvalidDataException("A Responses output item ended more than once.");
        }

        var type = RequiredString(item, "type");
        switch (slot.Kind)
        {
            case SlotKind.Reasoning when type == "reasoning":
                var reasoningText = JoinContentText(item, "summary", "content");
                ReplaceIfNonEmpty(slot.Buffer, reasoningText);
                slot.Signature = item.GetRawText();
                AddCharacters(slot.Signature.Length);
                break;
            case SlotKind.Text when type == "message":
                var text = JoinMessageText(item);
                ReplaceIfNonEmpty(slot.Buffer, text);
                slot.Phase = OptionalString(item, "phase");
                var id = RequiredString(item, "id");
                slot.Signature = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["v"] = 1,
                    ["id"] = id,
                    ["phase"] = slot.Phase,
                });
                break;
            case SlotKind.FunctionTool when type == "function_call":
                ReplaceIfPresent(slot.Buffer, OptionalString(item, "arguments"));
                slot.Namespace = OptionalString(item, "namespace") ?? slot.Namespace;
                break;
            case SlotKind.CustomTool when type == "custom_tool_call":
                ReplaceCustomInput(slot, OptionalString(item, "input") ?? slot.CustomInput.ToString(), updates, close: true);
                slot.Namespace = OptionalString(item, "namespace") ?? slot.Namespace;
                break;
            default:
                throw new InvalidDataException("A Responses output item changed type before completion.");
        }

        slot.Ended = true;
        var kind = slot.Kind switch
        {
            SlotKind.Reasoning => ModelStreamEventKind.ReasoningEnded,
            SlotKind.Text => ModelStreamEventKind.TextEnded,
            _ => ModelStreamEventKind.ToolCallEnded,
        };
        updates.Add(ModelStreamEvent.Update(
            kind,
            Partial(),
            contentIndex: ContentIndex(outputIndex),
            toolCallId: slot.CallId,
            toolName: slot.Name));
    }

    private void ApplyTextDelta(
        JsonElement root,
        SlotKind expected,
        ModelStreamEventKind kind,
        ICollection<ModelStreamEvent> updates)
    {
        ApplyLiteralDelta(root, expected, RequiredString(root, "delta"), kind, updates);
    }

    private void ApplyLiteralDelta(
        JsonElement root,
        SlotKind expected,
        string delta,
        ModelStreamEventKind kind,
        ICollection<ModelStreamEvent> updates)
    {
        var outputIndex = RequiredIndex(root);
        var slot = RequiredSlot(outputIndex, expected);
        AddCharacters(delta.Length);
        slot.Buffer.Append(delta);
        updates.Add(ModelStreamEvent.Update(kind, Partial(), delta, ContentIndex(outputIndex)));
    }

    private void ApplyFunctionArgumentsDelta(JsonElement root, ICollection<ModelStreamEvent> updates)
    {
        var outputIndex = RequiredIndex(root);
        var slot = RequiredSlot(outputIndex, SlotKind.FunctionTool);
        var delta = RequiredString(root, "delta");
        AddCharacters(delta.Length);
        slot.Buffer.Append(delta);
        updates.Add(ModelStreamEvent.Update(
            ModelStreamEventKind.ToolCallDelta,
            Partial(),
            delta,
            ContentIndex(outputIndex),
            slot.CallId,
            slot.Name));
    }

    private void ApplyFunctionArgumentsDone(JsonElement root, ICollection<ModelStreamEvent> updates)
    {
        var outputIndex = RequiredIndex(root);
        var slot = RequiredSlot(outputIndex, SlotKind.FunctionTool);
        var complete = RequiredString(root, "arguments");
        if (!complete.StartsWith(slot.Buffer.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Completed tool arguments changed previously streamed content.");
        }

        var delta = complete.Substring(slot.Buffer.Length);
        if (delta.Length > 0)
        {
            AddCharacters(delta.Length);
            slot.Buffer.Append(delta);
            updates.Add(ModelStreamEvent.Update(
                ModelStreamEventKind.ToolCallDelta,
                Partial(),
                delta,
                ContentIndex(outputIndex),
                slot.CallId,
                slot.Name));
        }
    }

    private void ApplyCustomInputDelta(JsonElement root, ICollection<ModelStreamEvent> updates)
    {
        var outputIndex = RequiredIndex(root);
        var slot = RequiredSlot(outputIndex, SlotKind.CustomTool);
        ReplaceCustomInput(slot, slot.CustomInput + RequiredString(root, "delta"), updates, close: false);
    }

    private void ApplyCustomInputDone(JsonElement root, ICollection<ModelStreamEvent> updates)
    {
        var outputIndex = RequiredIndex(root);
        var slot = RequiredSlot(outputIndex, SlotKind.CustomTool);
        ReplaceCustomInput(slot, RequiredString(root, "input"), updates, close: true);
    }

    private void ReplaceCustomInput(
        OutputSlot slot,
        string nextInput,
        ICollection<ModelStreamEvent> updates,
        bool close)
    {
        var previous = slot.CustomInput.ToString();
        if (!nextInput.StartsWith(previous, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A custom-tool input changed non-monotonically.");
        }

        var inputDelta = nextInput.Substring(previous.Length);
        slot.CustomInput.Clear();
        slot.CustomInput.Append(nextInput);
        var property = slot.CustomProperty!;
        var targetJson = JsonSerializer.Serialize(new Dictionary<string, string> { [property] = nextInput });
        var currentJson = slot.Buffer.ToString();
        string? jsonDelta;
        if (!close)
        {
            var openTarget = targetJson.Substring(0, targetJson.Length - 2);
            jsonDelta = openTarget.StartsWith(currentJson, StringComparison.Ordinal)
                ? openTarget.Substring(currentJson.Length)
                : null;
            targetJson = openTarget;
        }
        else
        {
            jsonDelta = targetJson.StartsWith(currentJson, StringComparison.Ordinal)
                ? targetJson.Substring(currentJson.Length)
                : null;
        }

        if (jsonDelta is null)
        {
            throw new InvalidDataException("A custom-tool JSON projection changed non-monotonically.");
        }

        slot.Buffer.Clear();
        slot.Buffer.Append(targetJson);
        if (jsonDelta.Length > 0)
        {
            AddCharacters(inputDelta.Length);
            updates.Add(ModelStreamEvent.Update(
                ModelStreamEventKind.ToolCallDelta,
                Partial(),
                jsonDelta,
                ContentIndex(slot.OutputIndex),
                slot.CallId,
                slot.Name));
        }
    }

    private void CompleteResponse(JsonElement response)
    {
        if (_terminal)
        {
            throw new InvalidDataException("The Responses stream emitted more than one terminal response.");
        }

        _terminal = true;
        ReadResponseIdentity(response);
        BackfillReasoningSignatures(response);
        ReadUsage(response);
        var status = OptionalString(response, "status") ?? "completed";
        var incompleteReason = response.TryGetProperty("incomplete_details", out var details)
                               && details.ValueKind == JsonValueKind.Object
            ? OptionalString(details, "reason")
            : null;
        _rawStopReason = incompleteReason is null ? status : status + "." + incompleteReason;
        switch (status)
        {
            case "completed":
                _stopReason = _slots.Values.Any(slot => slot.Kind is SlotKind.FunctionTool or SlotKind.CustomTool)
                    ? ModelStopReason.ToolUse
                    : ModelStopReason.Stop;
                break;
            case "incomplete" when incompleteReason == "max_output_tokens":
                _stopReason = ModelStopReason.Length;
                break;
            case "incomplete":
                _stopReason = ModelStopReason.Error;
                _errorMessage = incompleteReason is null
                    ? "The provider returned an incomplete response without a reason."
                    : "The provider returned an incomplete response: " + incompleteReason;
                break;
            case "failed":
            case "cancelled":
                _stopReason = ModelStopReason.Error;
                _errorMessage = ReadFailure(response);
                break;
            case "in_progress":
            case "queued":
                _stopReason = ModelStopReason.Error;
                _errorMessage = "The streaming response terminated while still " + status + ".";
                break;
            default:
                _stopReason = ModelStopReason.Error;
                _errorMessage = "The provider returned unsupported response status '" + status + "'.";
                break;
        }
    }

    private void ReadResponseIdentity(JsonElement response)
    {
        ReadStableString(response, "id", ref _responseId, "response ID");
        ReadStableString(response, "model", ref _responseModel, "response model");
    }

    private void ReadUsage(JsonElement response)
    {
        if (!response.TryGetProperty("usage", out var usage) || usage.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        RequireKind(usage, JsonValueKind.Object, "Responses usage must be an object.");
        var input = NonNegativeLong(usage, "input_tokens");
        var output = NonNegativeLong(usage, "output_tokens");
        var cacheRead = 0L;
        var cacheWrite = 0L;
        var reasoning = 0L;
        if (usage.TryGetProperty("input_tokens_details", out var inputDetails)
            && inputDetails.ValueKind == JsonValueKind.Object)
        {
            cacheRead = NonNegativeLong(inputDetails, "cached_tokens");
            cacheWrite = NonNegativeLong(inputDetails, "cache_write_tokens");
        }

        if (usage.TryGetProperty("output_tokens_details", out var outputDetails)
            && outputDetails.ValueKind == JsonValueKind.Object)
        {
            reasoning = NonNegativeLong(outputDetails, "reasoning_tokens");
        }

        if (cacheRead + cacheWrite > input || reasoning > output)
        {
            throw new InvalidDataException("Responses usage contains inconsistent token subsets.");
        }

        _usage = new ModelUsage(input - cacheRead - cacheWrite, output, cacheRead, cacheWrite, reasoning);
    }

    private void BackfillReasoningSignatures(JsonElement response)
    {
        if (!response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in output.EnumerateArray())
        {
            if (OptionalString(item, "type") != "reasoning")
            {
                continue;
            }

            var id = OptionalString(item, "id");
            if (id is null)
            {
                continue;
            }

            var slot = _slots.Values.FirstOrDefault(candidate => candidate.Kind == SlotKind.Reasoning
                                                               && candidate.Signature?.Contains(id, StringComparison.Ordinal) == true);
            if (slot is not null)
            {
                slot.Signature = item.GetRawText();
            }
        }
    }

    private OutputSlot RequiredSlot(int outputIndex, SlotKind expected)
    {
        if (!_slots.TryGetValue(outputIndex, out var slot) || slot.Kind != expected || slot.Ended)
        {
            throw new InvalidDataException("A Responses delta referenced a missing, ended, or incompatible output item.");
        }

        return slot;
    }

    private int ContentIndex(int outputIndex) => _slots.Keys.TakeWhile(key => key != outputIndex).Count();

    private void ReplaceIfNonEmpty(StringBuilder buffer, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        AddCharacters(Math.Max(0, value.Length - buffer.Length));
        buffer.Clear();
        buffer.Append(value);
    }

    private void ReplaceIfPresent(StringBuilder buffer, string? value)
    {
        if (value is not null)
        {
            ReplaceIfNonEmpty(buffer, value);
        }
    }

    private void AddCharacters(int count)
    {
        _characters += count;
        if (_characters > _maximumCharacters)
        {
            throw new InvalidDataException("The accumulated Responses output exceeded the configured size limit.");
        }
    }

    private static string JoinContentText(JsonElement item, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (!item.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var values = array.EnumerateArray()
                .Select(element => OptionalString(element, "text"))
                .Where(value => value is not null)
                .ToArray();
            if (values.Length > 0)
            {
                return string.Join("\n\n", values!);
            }
        }

        return string.Empty;
    }

    private static string JoinMessageText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Concat(content.EnumerateArray().Select(part =>
            OptionalString(part, "text") ?? OptionalString(part, "refusal") ?? string.Empty));
    }

    private static AgentTextPhase? ParsePhase(string? phase) => phase switch
    {
        "commentary" => AgentTextPhase.Commentary,
        "final_answer" => AgentTextPhase.FinalAnswer,
        _ => null,
    };

    private static string ReadFailure(JsonElement response)
    {
        if (response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            return (OptionalString(error, "code") ?? "unknown") + ": "
                   + (OptionalString(error, "message") ?? "No provider message was supplied.");
        }

        return "The provider returned a failed response without error details.";
    }

    private static int RequiredIndex(JsonElement root)
    {
        if (!root.TryGetProperty("output_index", out var value)
            || !value.TryGetInt32(out var index)
            || index < 0)
        {
            throw new InvalidDataException("A Responses output index must be a non-negative integer.");
        }

        return index;
    }

    private static JsonElement RequiredObject(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Responses event field '{property}' must be an object.");
        }

        return value;
    }

    private static string RequiredString(JsonElement root, string property) =>
        OptionalString(root, property)
        ?? throw new InvalidDataException($"Responses event field '{property}' must be a non-empty string.");

    private static string? OptionalString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Responses event field '{property}' must be a string or null.");
        }

        return value.GetString();
    }

    private static long NonNegativeLong(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            return 0;
        }

        if (!value.TryGetInt64(out var number) || number < 0)
        {
            throw new InvalidDataException($"Responses usage field '{property}' must be a non-negative integer.");
        }

        return number;
    }

    private static void ReadStableString(
        JsonElement root,
        string property,
        ref string? destination,
        string label)
    {
        var incoming = OptionalString(root, property);
        if (incoming is null)
        {
            return;
        }

        if (destination is not null && destination != incoming)
        {
            throw new InvalidDataException("The Responses stream changed its " + label + ".");
        }

        destination = incoming;
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

    private static void RequireKind(JsonElement value, JsonValueKind expected, string message)
    {
        if (value.ValueKind != expected)
        {
            throw new InvalidDataException(message);
        }
    }

    private static void EnsureUnambiguous(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("The Responses stream contains duplicate JSON property names.");
                }

                EnsureUnambiguous(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                EnsureUnambiguous(item);
            }
        }
    }

    private enum SlotKind
    {
        Reasoning,
        Text,
        FunctionTool,
        CustomTool,
    }

    private sealed class OutputSlot
    {
        public OutputSlot(int outputIndex, SlotKind kind)
        {
            OutputIndex = outputIndex;
            Kind = kind;
        }

        public int OutputIndex { get; }

        public SlotKind Kind { get; }

        public StringBuilder Buffer { get; } = new();

        public StringBuilder CustomInput { get; } = new();

        public string? CustomProperty { get; set; }

        public string? Signature { get; set; }

        public string? Phase { get; set; }

        public string? CallId { get; set; }

        public string? ItemId { get; set; }

        public string? Name { get; set; }

        public string? Namespace { get; set; }

        public string? ThoughtSignature { get; set; }

        public bool Ended { get; set; }
    }
}
