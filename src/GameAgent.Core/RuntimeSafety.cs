using System.Buffers;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class JsonValueLimits
{
    public JsonValueLimits(
        int maxUtf8Bytes = 262_144,
        int maxDepth = 32,
        int maxNodes = 8_192,
        int maxStringUtf8Bytes = 65_536,
        int maxContainerItems = 2_048)
    {
        if (maxUtf8Bytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUtf8Bytes));
        }

        if (maxDepth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth));
        }

        if (maxNodes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxNodes));
        }

        if (maxStringUtf8Bytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxStringUtf8Bytes));
        }

        if (maxContainerItems < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxContainerItems));
        }

        MaxUtf8Bytes = maxUtf8Bytes;
        MaxDepth = maxDepth;
        MaxNodes = maxNodes;
        MaxStringUtf8Bytes = maxStringUtf8Bytes;
        MaxContainerItems = maxContainerItems;
    }

    public int MaxUtf8Bytes { get; }

    public int MaxDepth { get; }

    public int MaxNodes { get; }

    public int MaxStringUtf8Bytes { get; }

    public int MaxContainerItems { get; }
}

public sealed class RuntimeContentLimitException : ArgumentException
{
    public RuntimeContentLimitException(string parameterName, string limitCode, string message)
        : base(message, parameterName)
    {
        LimitCode = limitCode;
    }

    public string LimitCode { get; }
}

internal static class RuntimeInputGuard
{
    public static TOutput[] CopyBounded<TInput, TOutput>(
        IEnumerable<TInput> source,
        int maximumItems,
        Func<TInput, TOutput> snapshot,
        string parameterName,
        string limitCode,
        CancellationToken cancellationToken = default)
    {
        if (source is null)
        {
            throw new ArgumentNullException(parameterName);
        }
        if (maximumItems < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumItems));
        }
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var result = new List<TOutput>(
            Math.Min(maximumItems, 16));
        using var enumerator = source.GetEnumerator();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!enumerator.MoveNext())
            {
                break;
            }
            if (result.Count >= maximumItems)
            {
                throw Limit(parameterName, limitCode, maximumItems);
            }

            result.Add(snapshot(enumerator.Current));
        }

        return result.ToArray();
    }

    private static RuntimeContentLimitException Limit(
        string parameterName,
        string limitCode,
        int maximumItems)
    {
        return new RuntimeContentLimitException(
            parameterName,
            limitCode,
            $"The input collection exceeds {maximumItems} items.");
    }
}

internal static class RuntimeProtocolInputGuard
{
    public static AgentRun ValidateAgentRunBeforeSerialization(
        AgentRun value,
        JsonValueLimits limits,
        int maximumUtf8Bytes,
        string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }
        var snapshot = SnapshotAgentRun(
            value,
            limits.MaxContainerItems,
            parameterName,
            "agent_run_items_exceeded");
        var budget = new InputBudget(
            limits,
            maximumUtf8Bytes,
            parameterName,
            "agent_run_bytes_exceeded",
            "agent_run_items_exceeded");
        budget.AddString(snapshot.ProtocolVersion);
        budget.AddString(snapshot.SchemaVersion);
        budget.AddString(snapshot.RunId);
        budget.AddString(snapshot.AgentId);
        budget.AddString(snapshot.WorldId);
        budget.AddString(snapshot.SessionId);
        budget.AddString(snapshot.Trigger?.Type);
        budget.AddString(snapshot.Trigger?.SourceId);
        budget.AddStrings(snapshot.TriggerObservationIds);
        budget.AddString(snapshot.DecisionKey);
        budget.AddString(snapshot.BatchId);
        budget.AddString(snapshot.State);
        budget.AddString(snapshot.CurrentTurnId);
        budget.AddString(snapshot.Budget?.MaxCostUsd);
        budget.AddString(snapshot.Usage?.CostUsd);
        budget.AddStrings(snapshot.PendingOperationIds);
        budget.AddString(snapshot.TerminalReason);
        budget.AddString(snapshot.CompletionIntent);
        budget.AddExtensions(snapshot.Extensions);
        ProtocolValidator.EnsureValid(snapshot);
        return snapshot;
    }

    public static ObservationEnvelope ValidateObservationBeforeSerialization(
        ObservationEnvelope value,
        JsonValueLimits limits,
        int maximumUtf8Bytes,
        string parameterName,
        int? maximumExtensionItems = null,
        string byteLimitCode = "observation_bytes_exceeded",
        string itemLimitCode = "observation_items_exceeded")
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }
        var extensionItemLimit = Math.Min(
            maximumExtensionItems ?? limits.MaxContainerItems,
            limits.MaxContainerItems);
        var snapshot = SnapshotObservation(
            value,
            limits.MaxContainerItems,
            extensionItemLimit,
            parameterName,
            itemLimitCode);
        var budget = new InputBudget(
            limits,
            maximumUtf8Bytes,
            parameterName,
            byteLimitCode,
            itemLimitCode);
        budget.AddString(snapshot.ProtocolVersion);
        budget.AddString(snapshot.SchemaVersion);
        budget.AddString(snapshot.ObservationId);
        budget.AddString(snapshot.WorldId);
        budget.AddString(snapshot.SessionId);
        budget.AddString(snapshot.Source);
        budget.AddString(snapshot.Kind);
        budget.AddStrings(snapshot.SubjectIds);
        budget.AddString(snapshot.ContentType);
        budget.AddString(snapshot.SchemaRef);
        budget.AddString(snapshot.ContentSchemaVersion);
        budget.AddJson(snapshot.Payload);
        budget.AddString(snapshot.ResourceRef?.Uri);
        budget.AddString(snapshot.ResourceRef?.MediaType);
        budget.AddString(snapshot.ResourceRef?.Digest);
        budget.AddString(snapshot.StateVersion);
        budget.AddString(snapshot.Trust);
        budget.AddString(snapshot.Visibility?.Scope);
        budget.AddStrings(snapshot.Visibility?.AudienceIds);
        budget.AddString(snapshot.CacheKey);
        budget.AddExtensions(
            snapshot.Extensions,
            extensionItemLimit);
        ProtocolValidator.EnsureValid(snapshot);
        return snapshot;
    }

    public static ToolDescriptor ValidateToolBeforeSerialization(
        ToolDescriptor value,
        JsonValueLimits limits,
        int maximumUtf8Bytes,
        string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }
        var snapshot = SnapshotTool(
            value,
            limits.MaxContainerItems,
            parameterName,
            "tool_descriptor_items_exceeded");
        var budget = new InputBudget(
            limits,
            maximumUtf8Bytes,
            parameterName,
            "tool_descriptor_bytes_exceeded",
            "tool_descriptor_items_exceeded");
        budget.AddString(snapshot.ProtocolVersion);
        budget.AddString(snapshot.SchemaVersion);
        budget.AddString(snapshot.Name);
        budget.AddString(snapshot.Version);
        budget.AddString(snapshot.Description);
        budget.AddJson(snapshot.ParametersSchema);
        budget.AddJson(snapshot.ResultSchema);
        budget.AddString(snapshot.Effect);
        budget.AddStrings(snapshot.ConflictScopes);
        budget.AddString(snapshot.ThreadAffinity);
        budget.AddString(snapshot.RetryPolicy);
        budget.AddString(snapshot.IdempotencyPolicy);
        budget.AddString(snapshot.Toolset);
        budget.AddString(snapshot.Visibility);
        budget.AddExtensions(snapshot.Extensions);
        ProtocolValidator.EnsureValid(snapshot);
        return snapshot;
    }

    private static AgentRun SnapshotAgentRun(
        AgentRun value,
        int maximumItems,
        string parameterName,
        string itemLimitCode)
    {
        return new AgentRun
        {
            ProtocolVersion = value.ProtocolVersion,
            SchemaVersion = value.SchemaVersion,
            Extensions = SnapshotExtensions(
                value.Extensions,
                maximumItems,
                parameterName,
                itemLimitCode)!,
            RunId = value.RunId,
            AgentId = value.AgentId,
            WorldId = value.WorldId,
            SessionId = value.SessionId,
            Trigger = value.Trigger is null
                ? null!
                : new AgentTrigger
                {
                    Type = value.Trigger.Type,
                    SourceId = value.Trigger.SourceId,
                    ScheduledFor = value.Trigger.ScheduledFor
                },
            TriggerObservationIds = SnapshotStrings(
                value.TriggerObservationIds,
                maximumItems,
                parameterName,
                itemLimitCode)!,
            DecisionKey = value.DecisionKey,
            BatchId = value.BatchId,
            State = value.State,
            Revision = value.Revision,
            CurrentTurnId = value.CurrentTurnId,
            RuntimeGeneration = value.RuntimeGeneration,
            Budget = value.Budget is null
                ? null!
                : new AgentBudget
                {
                    MaxTurns = value.Budget.MaxTurns,
                    MaxDurationMs = value.Budget.MaxDurationMs,
                    MaxTokens = value.Budget.MaxTokens,
                    MaxCostUsd = value.Budget.MaxCostUsd,
                    MaxActions = value.Budget.MaxActions
                },
            Usage = value.Usage is null
                ? null!
                : new AgentUsage
                {
                    Turns = value.Usage.Turns,
                    DurationMs = value.Usage.DurationMs,
                    InputTokens = value.Usage.InputTokens,
                    OutputTokens = value.Usage.OutputTokens,
                    CostUsd = value.Usage.CostUsd,
                    Actions = value.Usage.Actions,
                    HasUnaccountedUsage =
                        value.Usage.HasUnaccountedUsage,
                    UnaccountedProviderAttempts =
                        value.Usage.UnaccountedProviderAttempts
                },
            PendingOperationIds = SnapshotStrings(
                value.PendingOperationIds,
                maximumItems,
                parameterName,
                itemLimitCode)!,
            TerminalReason = value.TerminalReason,
            CompletionIntent = value.CompletionIntent,
            CreatedAt = value.CreatedAt,
            UpdatedAt = value.UpdatedAt
        };
    }

    private static ObservationEnvelope SnapshotObservation(
        ObservationEnvelope value,
        int maximumItems,
        int maximumExtensionItems,
        string parameterName,
        string itemLimitCode)
    {
        return new ObservationEnvelope
        {
            ProtocolVersion = value.ProtocolVersion,
            SchemaVersion = value.SchemaVersion,
            Extensions = SnapshotExtensions(
                value.Extensions,
                maximumExtensionItems,
                parameterName,
                itemLimitCode)!,
            ObservationId = value.ObservationId,
            WorldId = value.WorldId,
            SessionId = value.SessionId,
            Source = value.Source,
            Kind = value.Kind,
            SubjectIds = SnapshotStrings(
                value.SubjectIds,
                maximumItems,
                parameterName,
                itemLimitCode)!,
            ContentType = value.ContentType,
            SchemaRef = value.SchemaRef,
            ContentSchemaVersion = value.ContentSchemaVersion,
            Payload = SnapshotJson(value.Payload),
            ResourceRef = value.ResourceRef is null
                ? null
                : new ResourceReference
                {
                    Uri = value.ResourceRef.Uri,
                    MediaType = value.ResourceRef.MediaType,
                    Digest = value.ResourceRef.Digest,
                    SizeBytes = value.ResourceRef.SizeBytes
                },
            ObservedAt = value.ObservedAt,
            TtlMs = value.TtlMs,
            Sequence = value.Sequence,
            StateVersion = value.StateVersion,
            Trust = value.Trust,
            Visibility = value.Visibility is null
                ? null!
                : new VisibilityRule
                {
                    Scope = value.Visibility.Scope,
                    AudienceIds = SnapshotStrings(
                        value.Visibility.AudienceIds,
                        maximumItems,
                        parameterName,
                        itemLimitCode)!
                },
            Priority = value.Priority,
            CacheKey = value.CacheKey
        };
    }

    private static ToolDescriptor SnapshotTool(
        ToolDescriptor value,
        int maximumItems,
        string parameterName,
        string itemLimitCode)
    {
        return new ToolDescriptor
        {
            ProtocolVersion = value.ProtocolVersion,
            SchemaVersion = value.SchemaVersion,
            Extensions = SnapshotExtensions(
                value.Extensions,
                maximumItems,
                parameterName,
                itemLimitCode)!,
            Name = value.Name,
            Version = value.Version,
            Description = value.Description,
            ParametersSchema = SnapshotJson(value.ParametersSchema),
            ResultSchema = SnapshotJson(value.ResultSchema),
            Effect = value.Effect,
            ConflictScopes = SnapshotStrings(
                value.ConflictScopes,
                maximumItems,
                parameterName,
                itemLimitCode)!,
            ThreadAffinity = value.ThreadAffinity,
            TimeoutMs = value.TimeoutMs,
            RetryPolicy = value.RetryPolicy,
            IdempotencyPolicy = value.IdempotencyPolicy,
            Toolset = value.Toolset,
            Visibility = value.Visibility
        };
    }

    private static List<string>? SnapshotStrings(
        List<string>? values,
        int maximumItems,
        string parameterName,
        string itemLimitCode)
    {
        if (values is null)
        {
            return null;
        }

        var count = values.Count;
        if (count > maximumItems)
        {
            throw ItemLimit(parameterName, itemLimitCode, maximumItems);
        }

        var snapshot = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            var value = values[index];
            if (value is null)
            {
                throw NullCollectionElement(parameterName);
            }

            snapshot.Add(value);
        }

        return snapshot;
    }

    private static Dictionary<string, JsonElement>? SnapshotExtensions(
        Dictionary<string, JsonElement>? extensions,
        int maximumItems,
        string parameterName,
        string itemLimitCode)
    {
        if (extensions is null)
        {
            return null;
        }

        var count = extensions.Count;
        if (count > maximumItems)
        {
            throw ItemLimit(parameterName, itemLimitCode, maximumItems);
        }

        var snapshot = new Dictionary<string, JsonElement>(
            count,
            StringComparer.Ordinal);
        var enumerated = 0;
        var enumerator = extensions.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (enumerated >= maximumItems)
            {
                throw ItemLimit(
                    parameterName,
                    itemLimitCode,
                    maximumItems);
            }

            var pair = enumerator.Current;
            if (pair.Key is null)
            {
                throw NullCollectionElement(parameterName);
            }

            snapshot.Add(pair.Key, pair.Value);
            enumerated++;
        }

        return snapshot;
    }

    private static JsonElement? SnapshotJson(JsonElement? value)
    {
        return value.HasValue
            ? SnapshotJson(value.Value)
            : null;
    }

    private static JsonElement SnapshotJson(JsonElement value)
    {
        return value;
    }

    private static RuntimeContentLimitException ItemLimit(
        string parameterName,
        string itemLimitCode,
        int maximumItems)
    {
        return new RuntimeContentLimitException(
            parameterName,
            itemLimitCode,
            $"A protocol collection exceeds {maximumItems} items.");
    }

    private static ArgumentException NullCollectionElement(
        string parameterName)
    {
        return new ArgumentException(
            "Protocol collections cannot contain null elements.",
            parameterName);
    }

    private sealed class InputBudget
    {
        private readonly JsonValueLimits _limits;
        private readonly int _maximumUtf8Bytes;
        private readonly string _parameterName;
        private readonly string _byteLimitCode;
        private readonly string _itemLimitCode;
        private long _utf8Bytes;

        public InputBudget(
            JsonValueLimits limits,
            int maximumUtf8Bytes,
            string parameterName,
            string byteLimitCode,
            string itemLimitCode)
        {
            _limits = limits ?? throw new ArgumentNullException(nameof(limits));
            if (maximumUtf8Bytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumUtf8Bytes));
            }

            _maximumUtf8Bytes = Math.Min(
                maximumUtf8Bytes,
                limits.MaxUtf8Bytes);
            _parameterName = parameterName;
            _byteLimitCode = byteLimitCode;
            _itemLimitCode = itemLimitCode;
            Charge(256);
        }

        public void AddString(string? value)
        {
            if (value is null)
            {
                return;
            }

            var remaining = _maximumUtf8Bytes - _utf8Bytes;
            if (remaining <= 2 || value.Length > remaining - 2)
            {
                ThrowByteLimit();
            }

            var rawUtf8Bytes = Encoding.UTF8.GetByteCount(value);
            if (rawUtf8Bytes > _limits.MaxStringUtf8Bytes)
            {
                throw new RuntimeContentLimitException(
                    _parameterName,
                    "json_string_bytes_exceeded",
                    $"A protocol string exceeds "
                    + $"{_limits.MaxStringUtf8Bytes} UTF-8 bytes.");
            }
            if (rawUtf8Bytes > remaining - 2)
            {
                ThrowByteLimit();
            }

            var encodedUtf8Bytes = JsonEncodedText
                .Encode(value)
                .EncodedUtf8Bytes
                .Length;
            Charge(checked(encodedUtf8Bytes + 2));
        }

        public void AddStrings(IReadOnlyCollection<string>? values)
        {
            if (values is null)
            {
                return;
            }

            if (values.Count > _limits.MaxContainerItems)
            {
                ThrowItemLimit(_limits.MaxContainerItems);
            }

            Charge(checked(2L + Math.Max(0, values.Count - 1)));
            foreach (var value in values)
            {
                AddString(value);
            }
        }

        public void AddJson(JsonElement? value)
        {
            if (!value.HasValue)
            {
                return;
            }

            Charge(
                JsonValueInspector.ValidateAndMeasure(
                    value.Value,
                    _limits,
                    _parameterName));
        }

        public void AddExtensions(
            IReadOnlyDictionary<string, JsonElement>? extensions,
            int? maximumItems = null)
        {
            if (extensions is null)
            {
                return;
            }

            var itemLimit = Math.Min(
                maximumItems ?? _limits.MaxContainerItems,
                _limits.MaxContainerItems);
            if (extensions.Count > itemLimit)
            {
                ThrowItemLimit(itemLimit);
            }

            Charge(checked(2L + Math.Max(0, extensions.Count - 1)));
            foreach (var extension in extensions)
            {
                AddString(extension.Key);
                Charge(1);
                AddJson(extension.Value);
            }
        }

        private void Charge(long bytes)
        {
            if (bytes < 0 || bytes > _maximumUtf8Bytes - _utf8Bytes)
            {
                ThrowByteLimit();
            }

            _utf8Bytes += bytes;
        }

        private void ThrowByteLimit()
        {
            throw new RuntimeContentLimitException(
                _parameterName,
                _byteLimitCode,
                $"Protocol input exceeds {_maximumUtf8Bytes} UTF-8 bytes.");
        }

        private void ThrowItemLimit(int maximumItems)
        {
            throw new RuntimeContentLimitException(
                _parameterName,
                _itemLimitCode,
                $"A protocol collection exceeds "
                + $"{maximumItems} items.");
        }
    }
}

public static class JsonValueInspector
{
    public static int ValidateAndMeasure(
        JsonElement value,
        JsonValueLimits limits,
        string parameterName)
    {
        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new RuntimeContentLimitException(
                parameterName,
                "json_undefined",
                "An undefined JSON value is not allowed.");
        }

        var exactUtf8Bytes = MeasureBounded(
            value,
            limits,
            parameterName);
        var state = new InspectionState(limits, parameterName);
        Inspect(value, 1, state);
        return exactUtf8Bytes;
    }

    private static int MeasureBounded(
        JsonElement value,
        JsonValueLimits limits,
        string parameterName)
    {
        using var buffer = new CountingBufferWriter(
            limits.MaxUtf8Bytes,
            parameterName);
        try
        {
            using var writer = new Utf8JsonWriter(
                buffer,
                new JsonWriterOptions
                {
                    MaxDepth = limits.MaxDepth
                });
            value.WriteTo(writer);
            writer.Flush();
        }
        catch (Exception exception)
            when (exception is JsonException
                  or InvalidOperationException)
        {
            throw new RuntimeContentLimitException(
                parameterName,
                "json_depth_exceeded",
                $"JSON depth exceeds {limits.MaxDepth}.");
        }

        return buffer.WrittenBytes;
    }

    private static void Inspect(JsonElement value, int depth, InspectionState state)
    {
        if (depth > state.Limits.MaxDepth)
        {
            throw new RuntimeContentLimitException(
                state.ParameterName,
                "json_depth_exceeded",
                $"JSON depth exceeds {state.Limits.MaxDepth}.");
        }

        state.Nodes++;
        if (state.Nodes > state.Limits.MaxNodes)
        {
            throw new RuntimeContentLimitException(
                state.ParameterName,
                "json_nodes_exceeded",
                $"JSON node count exceeds {state.Limits.MaxNodes}.");
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                InspectObject(value, depth, state);
                break;
            case JsonValueKind.Array:
                InspectArray(value, depth, state);
                break;
            case JsonValueKind.String:
                AddStringBytes(value.GetString() ?? string.Empty, state);
                break;
            case JsonValueKind.Number:
                state.Utf8Bytes = checked(state.Utf8Bytes + value.GetRawText().Length);
                break;
            case JsonValueKind.True:
                state.Utf8Bytes = checked(state.Utf8Bytes + 4);
                break;
            case JsonValueKind.False:
                state.Utf8Bytes = checked(state.Utf8Bytes + 5);
                break;
            case JsonValueKind.Null:
                state.Utf8Bytes = checked(state.Utf8Bytes + 4);
                break;
            default:
                throw new RuntimeContentLimitException(
                    state.ParameterName,
                    "json_kind_unsupported",
                    $"JSON kind '{value.ValueKind}' is not supported.");
        }
    }

    private static void InspectObject(JsonElement value, int depth, InspectionState state)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        state.Utf8Bytes = checked(state.Utf8Bytes + 2);
        foreach (var property in value.EnumerateObject())
        {
            count++;
            if (count > state.Limits.MaxContainerItems)
            {
                ThrowContainerLimit(state);
            }

            if (!names.Add(property.Name))
            {
                throw new RuntimeContentLimitException(
                    state.ParameterName,
                    "json_duplicate_property",
                    $"Duplicate JSON property '{property.Name}' is not allowed.");
            }

            AddStringBytes(property.Name, state);
            Inspect(property.Value, depth + 1, state);
        }
    }

    private static void InspectArray(JsonElement value, int depth, InspectionState state)
    {
        var count = 0;
        state.Utf8Bytes = checked(state.Utf8Bytes + 2);
        foreach (var item in value.EnumerateArray())
        {
            count++;
            if (count > state.Limits.MaxContainerItems)
            {
                ThrowContainerLimit(state);
            }

            Inspect(item, depth + 1, state);
        }
    }

    private static void AddStringBytes(string value, InspectionState state)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes > state.Limits.MaxStringUtf8Bytes)
        {
            throw new RuntimeContentLimitException(
                state.ParameterName,
                "json_string_bytes_exceeded",
                $"A JSON string exceeds {state.Limits.MaxStringUtf8Bytes} UTF-8 bytes.");
        }

        state.Utf8Bytes = checked(state.Utf8Bytes + bytes + 2);
    }

    private static void ThrowContainerLimit(InspectionState state)
    {
        throw new RuntimeContentLimitException(
            state.ParameterName,
            "json_container_items_exceeded",
            $"A JSON container exceeds {state.Limits.MaxContainerItems} items.");
    }

    private sealed class InspectionState
    {
        public InspectionState(JsonValueLimits limits, string parameterName)
        {
            Limits = limits;
            ParameterName = parameterName;
        }

        public JsonValueLimits Limits { get; }

        public string ParameterName { get; }

        public int Nodes { get; set; }

        public int Utf8Bytes { get; set; }
    }

    private sealed class CountingBufferWriter :
        IBufferWriter<byte>,
        IDisposable
    {
        private const int DefaultSizeHint = 256;
        private const int WriterSlackBytes = 4_096;

        private readonly int _maximumBytes;
        private readonly int _maximumBufferBytes;
        private readonly string _parameterName;
        private byte[]? _buffer;
        private int _written;

        public CountingBufferWriter(
            int maximumBytes,
            string parameterName)
        {
            _maximumBytes = maximumBytes;
            _maximumBufferBytes = (int)Math.Min(
                int.MaxValue,
                (long)maximumBytes + WriterSlackBytes);
            _parameterName = parameterName;
        }

        public int WrittenBytes => _written;

        public void Advance(int count)
        {
            if (count < 0
                || _buffer is null
                || count > _buffer.Length
                || count > _maximumBytes - _written)
            {
                ThrowByteLimit();
            }

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureBuffer(sizeHint);
            return _buffer!;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureBuffer(sizeHint);
            return _buffer!;
        }

        public void Dispose()
        {
            var buffer = _buffer;
            _buffer = null;
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(
                    buffer,
                    clearArray: true);
            }
        }

        private void EnsureBuffer(int sizeHint)
        {
            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sizeHint));
            }

            var required = sizeHint == 0
                ? DefaultSizeHint
                : sizeHint;
            if (required > _maximumBufferBytes)
            {
                ThrowByteLimit();
            }

            if (_buffer is not null
                && _buffer.Length >= required)
            {
                return;
            }

            var replacement = ArrayPool<byte>.Shared.Rent(required);
            var previous = _buffer;
            _buffer = replacement;
            if (previous is not null)
            {
                ArrayPool<byte>.Shared.Return(
                    previous,
                    clearArray: true);
            }
        }

        private void ThrowByteLimit()
        {
            throw new RuntimeContentLimitException(
                _parameterName,
                "json_bytes_exceeded",
                $"JSON content exceeds {_maximumBytes} UTF-8 bytes.");
        }
    }
}

internal static class RuntimeGuard
{
    public static string RequiredId(string? value, string parameterName)
    {
        var validated = RequiredUtf8(value, 128, parameterName);
        foreach (var character in validated)
        {
            var allowed = character is >= 'A' and <= 'Z'
                          || character is >= 'a' and <= 'z'
                          || character is >= '0' and <= '9'
                          || character is '.' or '_' or ':' or '-';
            if (!allowed)
            {
                throw new ArgumentException(
                    "An identifier may contain only ASCII letters, digits, '.', '_', ':', and '-'.",
                    parameterName);
            }
        }

        return validated;
    }

    public static string RequiredUtf8(string? value, int maxUtf8Bytes, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        if (Encoding.UTF8.GetByteCount(value) > maxUtf8Bytes)
        {
            throw new RuntimeContentLimitException(
                parameterName,
                "string_bytes_exceeded",
                $"The value exceeds {maxUtf8Bytes} UTF-8 bytes.");
        }

        return value;
    }

    public static IReadOnlyList<string> CopyStrings(
        IEnumerable<string>? values,
        int maxItems,
        int maxItemUtf8Bytes,
        string parameterName,
        bool sort,
        bool requireUnique)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var seen = requireUnique
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;

        foreach (var value in values)
        {
            if (result.Count >= maxItems)
            {
                throw new RuntimeContentLimitException(
                    parameterName,
                    "collection_items_exceeded",
                    $"The collection exceeds {maxItems} items.");
            }

            var validated = RequiredUtf8(value, maxItemUtf8Bytes, parameterName);
            if (seen is not null && !seen.Add(validated))
            {
                throw new ArgumentException(
                    $"Duplicate value '{validated}' is not allowed.",
                    parameterName);
            }

            result.Add(validated);
        }

        if (sort)
        {
            result.Sort(StringComparer.Ordinal);
        }

        return new ReadOnlyCollection<string>(result);
    }

    public static IReadOnlyDictionary<string, JsonElement> CopyExtensions(
        IReadOnlyDictionary<string, JsonElement>? extensions,
        JsonValueLimits limits,
        int maxItems = 64)
    {
        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }
        if (extensions is null)
        {
            return new ReadOnlyDictionary<string, JsonElement>(
                new Dictionary<string, JsonElement>(StringComparer.Ordinal));
        }
        if (maxItems < 0)
        {
            throw ExtensionItemLimit(maxItems);
        }

        var bounded = CopyExtensionEntries(extensions, maxItems);
        if (bounded.Count == 0)
        {
            return new ReadOnlyDictionary<string, JsonElement>(
                new Dictionary<string, JsonElement>(StringComparer.Ordinal));
        }

        var validated = new List<KeyValuePair<string, JsonElement>>(
            bounded.Count);
        foreach (var pair in bounded)
        {
            var key = RequiredUtf8(pair.Key, 128, nameof(extensions));
            JsonValueInspector.ValidateAndMeasure(
                pair.Value,
                limits,
                nameof(extensions));
            validated.Add(new(
                key,
                pair.Value.ValueKind == JsonValueKind.Undefined
                    ? default
                    : pair.Value.Clone()));
        }

        validated.Sort(
            static (left, right) =>
                StringComparer.Ordinal.Compare(left.Key, right.Key));
        var copied = new Dictionary<string, JsonElement>(
            validated.Count,
            StringComparer.Ordinal);
        foreach (var pair in validated)
        {
            copied.Add(pair.Key, pair.Value);
        }

        return new ReadOnlyDictionary<string, JsonElement>(copied);
    }

    private static List<KeyValuePair<string, JsonElement>>
        CopyExtensionEntries(
            IReadOnlyDictionary<string, JsonElement> extensions,
            int maxItems)
    {
        if (extensions is Dictionary<string, JsonElement> dictionary)
        {
            var count = dictionary.Count;
            if (count > maxItems)
            {
                throw ExtensionItemLimit(maxItems);
            }

            var result =
                new List<KeyValuePair<string, JsonElement>>(count);
            var enumerator = dictionary.GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (result.Count >= maxItems)
                {
                    throw ExtensionItemLimit(maxItems);
                }

                result.Add(enumerator.Current);
            }

            return result;
        }

        var bounded = new List<KeyValuePair<string, JsonElement>>(
            Math.Min(Math.Max(maxItems, 0), 16));
        using var interfaceEnumerator = extensions.GetEnumerator();
        while (interfaceEnumerator.MoveNext())
        {
            if (bounded.Count >= maxItems)
            {
                throw ExtensionItemLimit(maxItems);
            }

            bounded.Add(interfaceEnumerator.Current);
        }

        return bounded;
    }

    private static RuntimeContentLimitException ExtensionItemLimit(
        int maxItems)
    {
        return new RuntimeContentLimitException(
            "extensions",
            "extension_items_exceeded",
            $"Extensions exceed {maxItems} items.");
    }
}

internal sealed class CanonicalDigestBuilder
{
    private readonly StringBuilder _builder = new();

    public void Add(string name, string? value)
    {
        AppendLengthPrefixed(name);
        AppendLengthPrefixed(value ?? string.Empty);
    }

    public void Add(string name, long value)
    {
        Add(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Add(string name, IEnumerable<string> values)
    {
        AppendLengthPrefixed(name);
        var materialized = values as IReadOnlyCollection<string> ?? values.ToArray();
        _builder.Append(materialized.Count);
        _builder.Append(':');
        foreach (var value in materialized)
        {
            AppendLengthPrefixed(value);
        }
    }

    public void Add(string name, JsonElement value)
    {
        AppendLengthPrefixed(name);
        WriteCanonicalJson(_builder, value);
    }

    public string Finish()
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(_builder.ToString());
        var digest = sha.ComputeHash(bytes);
        var result = new StringBuilder(digest.Length * 2);
        foreach (var item in digest)
        {
            result.Append(item.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    private void AppendLengthPrefixed(string value)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        _builder.Append(bytes);
        _builder.Append(':');
        _builder.Append(value);
    }

    private static void WriteCanonicalJson(StringBuilder output, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                var firstProperty = true;
                foreach (var property in value
                             .EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty)
                    {
                        output.Append(',');
                    }

                    firstProperty = false;
                    WriteJsonString(output, property.Name);
                    output.Append(':');
                    WriteCanonicalJson(output, property.Value);
                }

                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        output.Append(',');
                    }

                    firstItem = false;
                    WriteCanonicalJson(output, item);
                }

                output.Append(']');
                break;
            case JsonValueKind.String:
                WriteJsonString(output, value.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
                output.Append(value.GetRawText());
                break;
            case JsonValueKind.True:
                output.Append("true");
                break;
            case JsonValueKind.False:
                output.Append("false");
                break;
            case JsonValueKind.Null:
                output.Append("null");
                break;
            default:
                throw new ArgumentException("Undefined JSON cannot be canonicalized.", nameof(value));
        }
    }

    private static void WriteJsonString(StringBuilder output, string value)
    {
        output.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    output.Append("\\\"");
                    break;
                case '\\':
                    output.Append("\\\\");
                    break;
                case '\b':
                    output.Append("\\b");
                    break;
                case '\f':
                    output.Append("\\f");
                    break;
                case '\n':
                    output.Append("\\n");
                    break;
                case '\r':
                    output.Append("\\r");
                    break;
                case '\t':
                    output.Append("\\t");
                    break;
                default:
                    if (character < 0x20)
                    {
                        output.Append("\\u");
                        output.Append(((int)character).ToString(
                            "x4",
                            System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        output.Append(character);
                    }

                    break;
            }
        }

        output.Append('"');
    }
}
