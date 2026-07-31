using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class RuntimeTraceAnalysisOptions
{
    public int MaxEvents { get; set; } = 10_000;

    public int MaxUtf8Bytes { get; set; } = 16 * 1_048_576;

    public int MaxEventUtf8Bytes { get; set; } = 4 * 1_048_576;

    public int MaxJsonDepth { get; set; } = 32;

    public int MaxJsonNodesPerEvent { get; set; } = 65_536;

    internal RuntimeTraceAnalysisOptions Snapshot()
    {
        if (MaxEvents is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEvents));
        }

        if (MaxUtf8Bytes is < 1_024 or > 512 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxUtf8Bytes));
        }

        if (MaxEventUtf8Bytes is < 1_024 or > 64 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxEventUtf8Bytes));
        }

        if (MaxJsonDepth is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxJsonDepth));
        }

        if (MaxJsonNodesPerEvent is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxJsonNodesPerEvent));
        }

        return new RuntimeTraceAnalysisOptions
        {
            MaxEvents = MaxEvents,
            MaxUtf8Bytes = MaxUtf8Bytes,
            MaxEventUtf8Bytes = Math.Min(
                MaxEventUtf8Bytes,
                MaxUtf8Bytes),
            MaxJsonDepth = MaxJsonDepth,
            MaxJsonNodesPerEvent = MaxJsonNodesPerEvent
        };
    }
}

public sealed class RuntimeTraceAnalysis
{
    internal RuntimeTraceAnalysis(
        RuntimeRunProjection projection,
        RuntimeTrajectory trajectory,
        IReadOnlyList<string> eventKinds,
        long materializedUtf8Bytes)
    {
        Projection = projection;
        Trajectory = trajectory;
        EventKinds = eventKinds;
        MaterializedUtf8Bytes = materializedUtf8Bytes;
    }

    public RuntimeRunProjection Projection { get; }

    public RuntimeTrajectory Trajectory { get; }

    public IReadOnlyList<string> EventKinds { get; }

    public long MaterializedUtf8Bytes { get; }
}

public sealed class RuntimeTrajectory
{
    internal RuntimeTrajectory(
        IReadOnlyList<RuntimeTrajectoryEvent> events,
        IReadOnlyList<RuntimeTrajectoryTurn> turns,
        IReadOnlyList<RuntimeTrajectoryMessage> messages,
        IReadOnlyList<RuntimeTrajectoryToolCall> toolCalls,
        IReadOnlyList<RuntimeTrajectoryAction> actions,
        IReadOnlyList<RuntimeTrajectoryProviderAttempt> providerAttempts,
        RuntimeTrajectoryUsage usage,
        bool budgetCompliant,
        IReadOnlyList<string> assertionFailureCodes,
        string digest)
    {
        Events = events;
        Turns = turns;
        Messages = messages;
        ToolCalls = toolCalls;
        Actions = actions;
        ProviderAttempts = providerAttempts;
        Usage = usage;
        BudgetCompliant = budgetCompliant;
        AssertionFailureCodes = assertionFailureCodes;
        Digest = digest;
    }

    public IReadOnlyList<RuntimeTrajectoryEvent> Events { get; }

    public IReadOnlyList<RuntimeTrajectoryTurn> Turns { get; }

    public IReadOnlyList<RuntimeTrajectoryMessage> Messages { get; }

    public IReadOnlyList<RuntimeTrajectoryToolCall> ToolCalls { get; }

    public IReadOnlyList<RuntimeTrajectoryAction> Actions { get; }

    public IReadOnlyList<RuntimeTrajectoryProviderAttempt> ProviderAttempts
    {
        get;
    }

    public RuntimeTrajectoryUsage Usage { get; }

    public bool BudgetCompliant { get; }

    public IReadOnlyList<string> AssertionFailureCodes { get; }

    public string Digest { get; }
}

public sealed class RuntimeTrajectoryEvent
{
    internal RuntimeTrajectoryEvent(
        string eventId,
        string? runId,
        string? turnId,
        long sequence,
        string kind,
        string durability,
        long runtimeGeneration,
        string? attemptId,
        string? streamAttemptId,
        string? providerId,
        string? modelId,
        string? transportDialect,
        string? capabilityDigest,
        string? routeDigest,
        string? reasonCode,
        DateTimeOffset timestamp,
        string payloadDigest,
        string eventDigest)
    {
        EventId = eventId;
        RunId = runId;
        TurnId = turnId;
        Sequence = sequence;
        Kind = kind;
        Durability = durability;
        RuntimeGeneration = runtimeGeneration;
        AttemptId = attemptId;
        StreamAttemptId = streamAttemptId;
        ProviderId = providerId;
        ModelId = modelId;
        TransportDialect = transportDialect;
        CapabilityDigest = capabilityDigest;
        RouteDigest = routeDigest;
        ReasonCode = reasonCode;
        Timestamp = timestamp;
        PayloadDigest = payloadDigest;
        EventDigest = eventDigest;
    }

    public string EventId { get; }

    public string? RunId { get; }

    public string? TurnId { get; }

    public long Sequence { get; }

    public string Kind { get; }

    public string Durability { get; }

    public long RuntimeGeneration { get; }

    public string? AttemptId { get; }

    public string? StreamAttemptId { get; }

    public string? ProviderId { get; }

    public string? ModelId { get; }

    public string? TransportDialect { get; }

    public string? CapabilityDigest { get; }

    public string? RouteDigest { get; }

    public string? ReasonCode { get; }

    public DateTimeOffset Timestamp { get; }

    public string PayloadDigest { get; }

    public string EventDigest { get; }
}

public sealed class RuntimeTrajectoryTurn
{
    internal RuntimeTrajectoryTurn(
        string turnId,
        long? startedSequence,
        long? completedSequence,
        string? snapshotDigest)
    {
        TurnId = turnId;
        StartedSequence = startedSequence;
        CompletedSequence = completedSequence;
        SnapshotDigest = snapshotDigest;
    }

    public string TurnId { get; }

    public long? StartedSequence { get; }

    public long? CompletedSequence { get; }

    public string? SnapshotDigest { get; }
}

public sealed class RuntimeTrajectoryMessage
{
    internal RuntimeTrajectoryMessage(
        string messageId,
        string? turnId,
        string role,
        long sequence,
        string contentDigest,
        IReadOnlyList<string> toolCallIds,
        IReadOnlyList<string> toolResultIds)
    {
        MessageId = messageId;
        TurnId = turnId;
        Role = role;
        Sequence = sequence;
        ContentDigest = contentDigest;
        ToolCallIds = toolCallIds;
        ToolResultIds = toolResultIds;
    }

    public string MessageId { get; }

    public string? TurnId { get; }

    public string Role { get; }

    public long Sequence { get; }

    public string ContentDigest { get; }

    public IReadOnlyList<string> ToolCallIds { get; }

    public IReadOnlyList<string> ToolResultIds { get; }
}

public sealed class RuntimeTrajectoryToolCall
{
    internal RuntimeTrajectoryToolCall(
        string toolCallId,
        string? turnId,
        string? toolName,
        string? toolVersion,
        string? effect,
        long? messageSequence,
        long? startedSequence,
        long? resultSequence,
        string? resultStatus,
        string? argumentsDigest,
        string? resultDigest)
    {
        ToolCallId = toolCallId;
        TurnId = turnId;
        ToolName = toolName;
        ToolVersion = toolVersion;
        Effect = effect;
        MessageSequence = messageSequence;
        StartedSequence = startedSequence;
        ResultSequence = resultSequence;
        ResultStatus = resultStatus;
        ArgumentsDigest = argumentsDigest;
        ResultDigest = resultDigest;
    }

    public string ToolCallId { get; }

    public string? TurnId { get; }

    public string? ToolName { get; }

    public string? ToolVersion { get; }

    public string? Effect { get; }

    public long? MessageSequence { get; }

    public long? StartedSequence { get; }

    public long? ResultSequence { get; }

    public string? ResultStatus { get; }

    public string? ArgumentsDigest { get; }

    public string? ResultDigest { get; }
}

public sealed class RuntimeTrajectoryAction
{
    internal RuntimeTrajectoryAction(
        string operationId,
        string? turnId,
        string? toolCallId,
        string? actionName,
        string? actionVersion,
        long? requestSequence,
        long? receiptSequence,
        int receiptCount,
        long? receiptRevision,
        string? receiptStatus,
        string? argumentsDigest,
        string? requestDigest,
        string? receiptDigest)
    {
        OperationId = operationId;
        TurnId = turnId;
        ToolCallId = toolCallId;
        ActionName = actionName;
        ActionVersion = actionVersion;
        RequestSequence = requestSequence;
        ReceiptSequence = receiptSequence;
        ReceiptCount = receiptCount;
        ReceiptRevision = receiptRevision;
        ReceiptStatus = receiptStatus;
        ArgumentsDigest = argumentsDigest;
        RequestDigest = requestDigest;
        ReceiptDigest = receiptDigest;
    }

    public string OperationId { get; }

    public string? TurnId { get; }

    public string? ToolCallId { get; }

    public string? ActionName { get; }

    public string? ActionVersion { get; }

    public long? RequestSequence { get; }

    public long? ReceiptSequence { get; }

    public int ReceiptCount { get; }

    public long? ReceiptRevision { get; }

    public string? ReceiptStatus { get; }

    public string? ArgumentsDigest { get; }

    public string? RequestDigest { get; }

    public string? ReceiptDigest { get; }
}

public sealed class RuntimeTrajectoryProviderAttempt
{
    internal RuntimeTrajectoryProviderAttempt(
        string attemptKey,
        string? attemptId,
        string? streamAttemptId,
        string? turnId,
        string? providerId,
        string? modelId,
        string? transportDialect,
        string? capabilityDigest,
        string? routeDigest,
        string? routePolicyVersion,
        string? routePolicyDigest,
        long dispatchSequence,
        long? terminalSequence,
        string? terminalKind,
        long usageSamples,
        long inputTokens,
        long outputTokens,
        string costUsd)
    {
        AttemptKey = attemptKey;
        AttemptId = attemptId;
        StreamAttemptId = streamAttemptId;
        TurnId = turnId;
        ProviderId = providerId;
        ModelId = modelId;
        TransportDialect = transportDialect;
        CapabilityDigest = capabilityDigest;
        RouteDigest = routeDigest;
        RoutePolicyVersion = routePolicyVersion;
        RoutePolicyDigest = routePolicyDigest;
        DispatchSequence = dispatchSequence;
        TerminalSequence = terminalSequence;
        TerminalKind = terminalKind;
        UsageSamples = usageSamples;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CostUsd = costUsd;
    }

    public string AttemptKey { get; }

    public string? AttemptId { get; }

    public string? StreamAttemptId { get; }

    public string? TurnId { get; }

    public string? ProviderId { get; }

    public string? ModelId { get; }

    public string? TransportDialect { get; }

    public string? CapabilityDigest { get; }

    public string? RouteDigest { get; }

    public string? RoutePolicyVersion { get; }

    public string? RoutePolicyDigest { get; }

    public long DispatchSequence { get; }

    public long? TerminalSequence { get; }

    public string? TerminalKind { get; }

    public long UsageSamples { get; }

    public long InputTokens { get; }

    public long OutputTokens { get; }

    public string CostUsd { get; }
}

public sealed class RuntimeTrajectoryUsage
{
    internal RuntimeTrajectoryUsage(
        int turns,
        long durationMs,
        int inputTokens,
        int outputTokens,
        string costUsd,
        int providerUsageSamples,
        int? cacheReadTokens,
        int? cacheWriteTokens,
        int? cacheMissTokens,
        int? reasoningTokens,
        int? providerTotalTokens,
        string availability,
        int actions,
        bool hasUnaccountedUsage,
        int unaccountedProviderAttempts)
    {
        Turns = turns;
        DurationMs = durationMs;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CostUsd = costUsd;
        ProviderUsageSamples = providerUsageSamples;
        CacheReadTokens = cacheReadTokens;
        CacheWriteTokens = cacheWriteTokens;
        CacheMissTokens = cacheMissTokens;
        ReasoningTokens = reasoningTokens;
        ProviderTotalTokens = providerTotalTokens;
        Availability = availability;
        Actions = actions;
        HasUnaccountedUsage = hasUnaccountedUsage;
        UnaccountedProviderAttempts = unaccountedProviderAttempts;
    }

    public int Turns { get; }

    public long DurationMs { get; }

    public int InputTokens { get; }

    public int OutputTokens { get; }

    public long TotalTokens => (long)InputTokens + OutputTokens;

    public string CostUsd { get; }

    public int ProviderUsageSamples { get; }

    public int? CacheReadTokens { get; }

    public int? CacheWriteTokens { get; }

    public int? CacheMissTokens { get; }

    public int? ReasoningTokens { get; }

    public int? ProviderTotalTokens { get; }

    public string Availability { get; }

    public int Actions { get; }

    public bool HasUnaccountedUsage { get; }

    public int UnaccountedProviderAttempts { get; }

}

public sealed class RuntimeTraceAnalyzer
{
    private readonly RuntimeTraceAnalysisOptions _options;

    public RuntimeTraceAnalyzer(
        RuntimeTraceAnalysisOptions? options = null)
    {
        _options = (options ?? new RuntimeTraceAnalysisOptions()).Snapshot();
    }

    public RuntimeTraceAnalysis Analyze(IEnumerable<RuntimeEvent> events)
    {
        var snapshot = RuntimeTraceMaterializer.Materialize(
            events,
            _options,
            out var materializedUtf8Bytes);
        return RuntimeTraceAnalysisBuilder.Build(
            snapshot,
            materializedUtf8Bytes);
    }
}

internal static class RuntimeTraceMaterializer
{
    public static IReadOnlyList<RuntimeEvent> Materialize(
        IEnumerable<RuntimeEvent> events,
        RuntimeTraceAnalysisOptions options,
        out long materializedUtf8Bytes)
    {
        if (events is null)
        {
            throw new ArgumentNullException(nameof(events));
        }

        var result = new List<RuntimeEvent>(
            Math.Min(options.MaxEvents, 4_096));
        long totalBytes = 0;
        using var enumerator = events.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (result.Count >= options.MaxEvents)
            {
                throw Limit(
                    "trace_analysis_event_count_exceeded",
                    "The runtime trace contains too many events.");
            }

            var item = enumerator.Current;
            if (item is null)
            {
                throw new ArgumentException(
                    "Runtime trace analysis cannot contain null events.",
                    nameof(events));
            }

            Preflight(item, options);
            byte[] serialized;
            try
            {
                serialized = JsonSerializer.SerializeToUtf8Bytes(
                    item,
                    ProtocolJsonContext.Default.RuntimeEvent);
            }
            catch (Exception exception)
                when (exception is JsonException
                      or InvalidOperationException
                      or OverflowException)
            {
                throw Limit(
                    "trace_analysis_event_value_exceeded",
                    "A runtime trace event cannot be represented safely.");
            }

            var eventBytes = serialized.Length;
            if (eventBytes > options.MaxEventUtf8Bytes)
            {
                throw Limit(
                    "trace_analysis_event_value_exceeded",
                    "A runtime trace event exceeds its byte limit.");
            }

            totalBytes = checked(totalBytes + eventBytes + 1L);
            if (totalBytes > options.MaxUtf8Bytes)
            {
                throw Limit(
                    "trace_analysis_bytes_exceeded",
                    "The runtime trace exceeds its aggregate byte limit.");
            }

            result.Add(Clone(item));
        }

        materializedUtf8Bytes = totalBytes;
        return new ReadOnlyCollection<RuntimeEvent>(result);
    }

    private static void Preflight(
        RuntimeEvent value,
        RuntimeTraceAnalysisOptions options)
    {
        if (value.Extensions is null)
        {
            throw new ArgumentException(
                "Runtime trace event extensions cannot be null.",
                nameof(value));
        }

        try
        {
            long bytes = 512;
            AddString(value.ProtocolVersion, options, ref bytes);
            AddString(value.SchemaVersion, options, ref bytes);
            AddString(value.EventId, options, ref bytes);
            AddString(value.RunId, options, ref bytes);
            AddString(value.TurnId, options, ref bytes);
            AddString(value.Kind, options, ref bytes);
            AddString(value.Durability, options, ref bytes);
            AddString(value.AttemptId, options, ref bytes);
            AddString(value.StreamAttemptId, options, ref bytes);
            AddString(value.ProviderId, options, ref bytes);
            AddString(value.ModelId, options, ref bytes);
            AddString(value.TransportDialect, options, ref bytes);
            AddString(
                value.ProviderCapabilityDigest,
                options,
                ref bytes);
            AddString(value.ProviderRouteDigest, options, ref bytes);
            AddString(value.ReasonCode, options, ref bytes);
            var nodes = 0;
            AddJson(value.Payload, options, ref bytes, ref nodes);
            foreach (var pair in value.Extensions)
            {
                AddString(pair.Key, options, ref bytes);
                bytes = checked(bytes + 4);
                AddJson(pair.Value, options, ref bytes, ref nodes);
            }

            if (bytes > options.MaxEventUtf8Bytes)
            {
                throw Limit(
                    "trace_analysis_event_value_exceeded",
                    "A runtime trace event exceeds its value limit.");
            }
        }
        catch (RuntimeContentLimitException exception)
            when (!string.Equals(
                exception.LimitCode,
                "trace_analysis_event_value_exceeded",
                StringComparison.Ordinal))
        {
            throw Limit(
                "trace_analysis_event_value_exceeded",
                "A runtime trace event exceeds its JSON value limit.");
        }
        catch (OverflowException)
        {
            throw Limit(
                "trace_analysis_event_value_exceeded",
                "A runtime trace event exceeds its value limit.");
        }
    }

    private static void AddJson(
        JsonElement value,
        RuntimeTraceAnalysisOptions options,
        ref long bytes,
        ref int nodes)
    {
        var remainingBytes = options.MaxEventUtf8Bytes - bytes;
        var remainingNodes = options.MaxJsonNodesPerEvent - nodes;
        if (remainingBytes < 1 || remainingNodes < 1)
        {
            throw Limit(
                "trace_analysis_event_value_exceeded",
                "A runtime trace event exceeds its JSON value limit.");
        }

        var measurement = JsonValueInspector.ValidateAndMeasureDetailed(
            value,
            new JsonValueLimits(
                checked((int)remainingBytes),
                options.MaxJsonDepth,
                remainingNodes,
                checked((int)remainingBytes),
                remainingNodes),
            "events");
        bytes = checked(bytes + measurement.Utf8Bytes);
        nodes = checked(nodes + measurement.Nodes);
    }

    private static void AddString(
        string? value,
        RuntimeTraceAnalysisOptions options,
        ref long bytes)
    {
        if (value is null)
        {
            bytes = checked(bytes + 4);
            return;
        }

        var remaining = options.MaxEventUtf8Bytes - bytes;
        if (remaining < 2 || value.Length > remaining - 2)
        {
            throw Limit(
                "trace_analysis_event_value_exceeded",
                "A runtime trace event exceeds its value limit.");
        }

        var source = value.AsSpan();
        Span<char> buffer = stackalloc char[256];
        while (!source.IsEmpty)
        {
            var status = JavaScriptEncoder.Default.Encode(
                source,
                buffer,
                out var charsConsumed,
                out var charsWritten,
                isFinalBlock: true);
            var bytesWritten = Encoding.UTF8.GetByteCount(
                buffer[..charsWritten]);
            if (bytesWritten > remaining - 2)
            {
                throw Limit(
                    "trace_analysis_event_value_exceeded",
                    "A runtime trace event exceeds its value limit.");
            }

            bytes = checked(bytes + bytesWritten);
            remaining -= bytesWritten;
            source = source[charsConsumed..];
            if (status == OperationStatus.Done)
            {
                break;
            }

            if (status != OperationStatus.DestinationTooSmall
                || charsConsumed == 0 && bytesWritten == 0)
            {
                throw Limit(
                    "trace_analysis_event_value_exceeded",
                    "A runtime trace event exceeds its value limit.");
            }
        }

        bytes = checked(bytes + 2);
    }

    private static RuntimeEvent Clone(RuntimeEvent value)
    {
        var extensions = new Dictionary<string, JsonElement>(
            StringComparer.Ordinal);
        foreach (var pair in value.Extensions)
        {
            extensions.Add(pair.Key, pair.Value.Clone());
        }

        return new RuntimeEvent
        {
            ProtocolVersion = value.ProtocolVersion,
            SchemaVersion = value.SchemaVersion,
            Extensions = extensions,
            EventId = value.EventId,
            RunId = value.RunId,
            TurnId = value.TurnId,
            Sequence = value.Sequence,
            Kind = value.Kind,
            Durability = value.Durability,
            RuntimeGeneration = value.RuntimeGeneration,
            AttemptId = value.AttemptId,
            StreamAttemptId = value.StreamAttemptId,
            ProviderId = value.ProviderId,
            ModelId = value.ModelId,
            TransportDialect = value.TransportDialect,
            ProviderCapabilityDigest = value.ProviderCapabilityDigest,
            ProviderRouteDigest = value.ProviderRouteDigest,
            ReasonCode = value.ReasonCode,
            Timestamp = value.Timestamp,
            Payload = value.Payload.Clone()
        };
    }

    private static RuntimeContentLimitException Limit(
        string code,
        string message)
    {
        return new RuntimeContentLimitException("events", code, message);
    }
}

internal static class RuntimeTraceAnalysisBuilder
{
    private static readonly HashSet<string> TerminalKinds = new(
        new[]
        {
            RuntimeEventKinds.RunCompleted,
            RuntimeEventKinds.RunInterrupted,
            RuntimeEventKinds.RunFailed,
            RuntimeEventKinds.RunCancelled,
            RuntimeEventKinds.RunBudgetExhausted
        },
        StringComparer.Ordinal);

    public static RuntimeTraceAnalysis Build(
        IReadOnlyList<RuntimeEvent> events,
        long materializedUtf8Bytes)
    {
        var projectionAnomalies = new HashSet<string>(
            StringComparer.Ordinal);
        var assertions = new HashSet<string>(StringComparer.Ordinal);
        var eventIds = new HashSet<string?>(StringComparer.Ordinal);
        var eventKinds = new HashSet<string>(StringComparer.Ordinal);
        var trajectoryEvents = new List<RuntimeTrajectoryEvent>(
            events.Count);
        var turns = new Dictionary<string, TurnState>(
            StringComparer.Ordinal);
        var messages = new List<RuntimeTrajectoryMessage>();
        var messageIds = new HashSet<string>(StringComparer.Ordinal);
        var tools = new Dictionary<string, ToolState>(
            StringComparer.Ordinal);
        var actions = new Dictionary<string, ActionState>(
            StringComparer.Ordinal);
        var providers = new Dictionary<string, ProviderState>(
            StringComparer.Ordinal);
        string? runId = null;
        long lastSequence = -1;
        string? terminalKind = null;
        long? terminalSequence = null;
        var terminalCount = 0;
        var runStartCount = 0;
        var turnCount = 0;
        var toolStartCount = 0;
        var actionRequestCount = 0;
        var providerDispatchCount = 0;
        AgentUsage? finalUsage = null;
        AgentBudget? finalBudget = null;
        AgentUsage? previousUsage = null;
        AgentRun? previousCheckpoint = null;
        AgentRun? stableRun = null;

        foreach (var item in events)
        {
            var kind = item.Kind ?? string.Empty;
            eventKinds.Add(kind);
            if (runId is null)
            {
                runId = item.RunId;
            }
            else if (!string.Equals(runId, item.RunId, StringComparison.Ordinal))
            {
                projectionAnomalies.Add("projection_run_id_mismatch");
                assertions.Add("trajectory_run_id_mismatch");
            }

            if (lastSequence < 0
                    ? item.Sequence != 0
                    : lastSequence == long.MaxValue
                      || item.Sequence != lastSequence + 1)
            {
                projectionAnomalies.Add("projection_sequence_gap");
                assertions.Add("trajectory_sequence_gap");
            }

            if (!eventIds.Add(item.EventId))
            {
                projectionAnomalies.Add("projection_duplicate_event_id");
                assertions.Add("trajectory_duplicate_event_id");
            }

            if (ProtocolValidator.Validate(item).Count > 0)
            {
                assertions.Add("trajectory_protocol_event_invalid");
            }

            ValidateStableEventIdentity(item, stableRun, assertions);
            ValidateTurnScope(
                item,
                turns,
                hasStartedTurn: turnCount > 0,
                assertions);

            if (terminalSequence.HasValue
                && string.Equals(
                    item.Durability,
                    EventDurabilities.Durable,
                    StringComparison.Ordinal)
                && !IsAllowedAfterTerminal(kind))
            {
                assertions.Add("trajectory_event_after_terminal");
            }

            if (TerminalKinds.Contains(kind))
            {
                terminalKind = kind;
                terminalCount++;
                terminalSequence ??= item.Sequence;
            }

            if (kind == RuntimeEventKinds.RunStarted)
            {
                runStartCount++;
                if (trajectoryEvents.Count != 0)
                {
                    assertions.Add("trajectory_run_start_not_first");
                }
            }

            var payloadDigest = DigestJson("runtime-event-payload", item.Payload);
            var eventDigest = DigestJson(
                "runtime-event",
                ProtocolJson.ToElement(item));
            trajectoryEvents.Add(
                new RuntimeTrajectoryEvent(
                    item.EventId ?? string.Empty,
                    item.RunId,
                    item.TurnId,
                    item.Sequence,
                    kind,
                    item.Durability ?? string.Empty,
                    item.RuntimeGeneration,
                    item.AttemptId,
                    item.StreamAttemptId,
                    item.ProviderId,
                    item.ModelId,
                    item.TransportDialect,
                    item.ProviderCapabilityDigest,
                    item.ProviderRouteDigest,
                    item.ReasonCode,
                    item.Timestamp,
                    payloadDigest,
                    eventDigest));

            if (kind == RuntimeEventKinds.TurnStarted)
            {
                turnCount++;
                var turn = GetTurn(turns, item.TurnId, assertions);
                if (turn is not null)
                {
                    if (turn.StartedSequence.HasValue)
                    {
                        assertions.Add("trajectory_turn_duplicate_start");
                    }

                    turn.StartedSequence ??= item.Sequence;
                }
            }
            else if (kind == RuntimeEventKinds.TurnCompleted)
            {
                var turn = GetTurn(turns, item.TurnId, assertions);
                if (turn is not null)
                {
                    if (turn.CompletedSequence.HasValue)
                    {
                        assertions.Add("trajectory_turn_duplicate_completion");
                    }

                    turn.CompletedSequence ??= item.Sequence;
                }
            }
            else if (kind == RuntimeEventKinds.TurnSnapshot)
            {
                ProcessTurnSnapshot(item, turns, assertions);
            }
            else if (kind == RuntimeEventKinds.TranscriptMessage)
            {
                ProcessMessage(
                    item,
                    messages,
                    messageIds,
                    tools,
                    assertions);
            }
            else if (kind == RuntimeEventKinds.ToolStarted)
            {
                toolStartCount++;
                ProcessToolStarted(item, tools, assertions);
            }
            else if (kind == RuntimeEventKinds.ActionRequested)
            {
                actionRequestCount++;
                ProcessActionRequest(
                    item,
                    actions,
                    stableRun,
                    assertions);
            }
            else if (kind == RuntimeEventKinds.ActionReceived)
            {
                ProcessActionReceipt(
                    item,
                    actions,
                    receiptIsHostEvent: true,
                    stableRun,
                    assertions);
            }
            else if (kind == RuntimeEventKinds.ActionOutcomeUncertain)
            {
                ProcessActionReceipt(
                    item,
                    actions,
                    receiptIsHostEvent: false,
                    stableRun,
                    assertions);
            }
            else if (kind is RuntimeEventKinds.ToolCompleted
                     or RuntimeEventKinds.ToolFailed)
            {
                ProcessToolResult(
                    item,
                    actions,
                    tools,
                    stableRun,
                    assertions);
            }
            else if (kind == RuntimeEventKinds.ProviderDispatchStarted)
            {
                providerDispatchCount++;
                ProcessProviderDispatch(item, providers, assertions);
            }
            else if (kind is RuntimeEventKinds.ProviderDispatchKnownZero
                     or RuntimeEventKinds.ProviderUsageUncertain
                     or RuntimeEventKinds.ProviderResultCommitted
                     or RuntimeEventKinds.ProviderResultDiscarded)
            {
                ProcessProviderTerminal(item, providers, assertions);
            }

            var checkpointKind =
                RunCheckpointLifecycleValidator.IsCheckpointKind(kind);
            if (checkpointKind
                && TryDecodeAgentRun(item.Payload, out var run))
            {
                try
                {
                    previousCheckpoint =
                        RunCheckpointLifecycleValidator.ValidateAndClone(
                            item,
                            previousCheckpoint,
                            item.Sequence,
                            checked(item.Sequence + 1));
                    stableRun ??= previousCheckpoint;
                }
                catch (Exception exception)
                    when (exception is not OutOfMemoryException)
                {
                    assertions.Add(
                        "trajectory_run_checkpoint_lifecycle_invalid");
                }

                ValidateRunCheckpoint(
                    item,
                    run,
                    assertions);
                if (kind == RuntimeEventKinds.BudgetUpdated)
                {
                    AssignProviderUsage(
                        item,
                        run.Usage,
                        previousUsage,
                        providers,
                        assertions);
                }

                finalUsage = run.Usage;
                finalBudget = run.Budget;
                previousUsage = run.Usage;
            }
            else if (checkpointKind)
            {
                assertions.Add("trajectory_run_checkpoint_invalid");
            }

            lastSequence = item.Sequence;
        }

        if (terminalCount > 1)
        {
            assertions.Add("trajectory_multiple_terminal_events");
        }
        else if (terminalCount == 0)
        {
            assertions.Add("trajectory_terminal_missing");
        }

        if (runStartCount == 0)
        {
            assertions.Add("trajectory_run_start_missing");
        }
        else if (runStartCount > 1)
        {
            assertions.Add("trajectory_multiple_run_start_events");
        }

        if (stableRun is null)
        {
            assertions.Add("trajectory_stable_run_identity_missing");
        }

        FinalizeTurnAssertions(turns, assertions);
        FinalizeToolAssertions(tools, assertions);
        FinalizeActionAssertions(actions, tools, assertions);
        FinalizeProviderAssertions(providers, assertions);

        var usage = ToUsage(finalUsage);
        var budgetCompliant = IsBudgetCompliant(
            finalBudget,
            usage,
            terminalKind,
            assertions);
        var projection = new RuntimeRunProjection(
            runId,
            events.Count,
            lastSequence,
            terminalKind,
            turnCount,
            toolStartCount,
            actionRequestCount,
            providerDispatchCount,
            ReadOnlySorted(projectionAnomalies));
        var turnItems = turns.Values
            .OrderBy(item => item.FirstSequence)
            .ThenBy(item => item.TurnId, StringComparer.Ordinal)
            .Select(
                item => new RuntimeTrajectoryTurn(
                    item.TurnId,
                    item.StartedSequence,
                    item.CompletedSequence,
                    item.SnapshotDigest))
            .ToArray();
        var toolItems = tools.Values
            .OrderBy(item => item.FirstSequence)
            .ThenBy(item => item.ToolCallId, StringComparer.Ordinal)
            .Select(ToToolCall)
            .ToArray();
        var actionItems = actions.Values
            .OrderBy(item => item.FirstSequence)
            .ThenBy(item => item.OperationId, StringComparer.Ordinal)
            .Select(ToAction)
            .ToArray();
        var providerItems = providers.Values
            .OrderBy(item => item.DispatchSequence)
            .ThenBy(item => item.AttemptKey, StringComparer.Ordinal)
            .Select(ToProviderAttempt)
            .ToArray();
        var orderedAssertions = ReadOnlySorted(assertions);
        var digest = ComputeTrajectoryDigest(
            trajectoryEvents,
            turnItems,
            messages,
            toolItems,
            actionItems,
            providerItems,
            usage,
            budgetCompliant,
            orderedAssertions);
        var trajectory = new RuntimeTrajectory(
            ReadOnly(trajectoryEvents),
            ReadOnly(turnItems),
            ReadOnly(messages),
            ReadOnly(toolItems),
            ReadOnly(actionItems),
            ReadOnly(providerItems),
            usage,
            budgetCompliant,
            orderedAssertions,
            digest);
        return new RuntimeTraceAnalysis(
            projection,
            trajectory,
            new ReadOnlyCollection<string>(
                eventKinds.OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray()),
            materializedUtf8Bytes);
    }

    private static void ValidateStableEventIdentity(
        RuntimeEvent item,
        AgentRun? stableRun,
        ISet<string> assertions)
    {
        if (stableRun is null)
        {
            return;
        }

        if (!string.Equals(
                item.RunId,
                stableRun.RunId,
                StringComparison.Ordinal))
        {
            assertions.Add("trajectory_stable_run_identity_mismatch");
        }

        if (item.RuntimeGeneration != stableRun.RuntimeGeneration)
        {
            assertions.Add("trajectory_runtime_generation_mismatch");
        }
    }

    private static void ValidateTurnScope(
        RuntimeEvent item,
        IReadOnlyDictionary<string, TurnState> turns,
        bool hasStartedTurn,
        ISet<string> assertions)
    {
        if (string.Equals(
                item.Kind,
                RuntimeEventKinds.TurnStarted,
                StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(item.TurnId))
            {
                assertions.Add("trajectory_turn_id_missing");
            }

            return;
        }

        if (!IsTurnScopedKind(item.Kind))
        {
            return;
        }

        if (IsInitialTranscript(item, turns, hasStartedTurn))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(item.TurnId)
            || !turns.TryGetValue(item.TurnId, out var turn)
            || !turn.StartedSequence.HasValue
            || turn.StartedSequence.Value > item.Sequence)
        {
            assertions.Add("trajectory_turn_scope_not_started");
            return;
        }

        if (turn.CompletedSequence.HasValue
            && !string.Equals(
                item.Kind,
                RuntimeEventKinds.TurnCompleted,
                StringComparison.Ordinal)
            && item.Sequence > turn.CompletedSequence.Value)
        {
            assertions.Add("trajectory_turn_scope_after_completion");
        }
    }

    private static bool IsInitialTranscript(
        RuntimeEvent item,
        IReadOnlyDictionary<string, TurnState> turns,
        bool hasStartedTurn)
    {
        return string.Equals(
                item.Kind,
                RuntimeEventKinds.TranscriptMessage,
                StringComparison.Ordinal)
            && string.Equals(
                item.TurnId,
                "initial",
                StringComparison.Ordinal)
            && item.AttemptId is null
            && !hasStartedTurn
            && !turns.ContainsKey("initial");
    }

    private static bool IsTurnScopedKind(string kind)
    {
        return kind is RuntimeEventKinds.TurnCompleted
            or RuntimeEventKinds.TurnSnapshot
            or RuntimeEventKinds.TranscriptMessage
            or RuntimeEventKinds.AssistantDelta
            or RuntimeEventKinds.AssistantCompleted
            or RuntimeEventKinds.ToolStarted
            or RuntimeEventKinds.ToolCompleted
            or RuntimeEventKinds.ToolFailed
            or RuntimeEventKinds.ToolDisclosureChanged
            or RuntimeEventKinds.ActionRequested
            or RuntimeEventKinds.ActionReceived
            or RuntimeEventKinds.ActionOutcomeUncertain
            or RuntimeEventKinds.ActionReconciling
            or RuntimeEventKinds.GameContextAdvanced
            or RuntimeEventKinds.ProviderRetry
            or RuntimeEventKinds.ProviderFallback
            or RuntimeEventKinds.ProviderDispatchStarted
            or RuntimeEventKinds.ProviderDispatchKnownZero
            or RuntimeEventKinds.ProviderUsageUncertain
            or RuntimeEventKinds.ProviderResultCommitted
            or RuntimeEventKinds.ProviderResultDiscarded
            or RuntimeEventKinds.MemoryCommitPrepared
            or RuntimeEventKinds.MemoryCommitCompleted
            or RuntimeEventKinds.MemoryCommitSettled
            or RuntimeEventKinds.BudgetUpdated;
    }

    private static void ProcessTurnSnapshot(
        RuntimeEvent item,
        IDictionary<string, TurnState> turns,
        ISet<string> assertions)
    {
        TurnSnapshot snapshot;
        try
        {
            snapshot = ProtocolJson.DeserializeTurnSnapshot(
                item.Payload.GetRawText());
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            assertions.Add("trajectory_turn_snapshot_invalid");
            return;
        }

        if (ProtocolValidator.Validate(snapshot).Count > 0)
        {
            assertions.Add("trajectory_turn_snapshot_invalid");
        }

        if (!string.Equals(snapshot.RunId, item.RunId, StringComparison.Ordinal)
            || !string.Equals(
                snapshot.TurnId,
                item.TurnId,
                StringComparison.Ordinal)
            || snapshot.RuntimeGeneration != item.RuntimeGeneration)
        {
            assertions.Add("trajectory_turn_snapshot_identity_mismatch");
        }

        var turn = GetTurn(turns, item.TurnId, assertions);
        if (turn is not null)
        {
            if (turn.SnapshotDigest is not null)
            {
                assertions.Add("trajectory_turn_duplicate_snapshot");
            }

            turn.SnapshotDigest ??=
                DigestJson("turn-snapshot", item.Payload);
            turn.FirstSequence = Math.Min(turn.FirstSequence, item.Sequence);
        }
    }

    private static void ProcessMessage(
        RuntimeEvent item,
        ICollection<RuntimeTrajectoryMessage> messages,
        ISet<string> messageIds,
        IDictionary<string, ToolState> tools,
        ISet<string> assertions)
    {
        NormalizedMessage message;
        try
        {
            message = NormalizedMessageJournalCodec.Decode(item.Payload);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            assertions.Add("trajectory_message_payload_invalid");
            return;
        }

        if (!messageIds.Add(message.MessageId))
        {
            assertions.Add("trajectory_duplicate_message_id");
        }

        var callIds = new List<string>();
        var resultIds = new List<string>();
        foreach (var part in message.Parts)
        {
            if (part.Type == NormalizedPartTypes.ToolCall
                && !string.IsNullOrWhiteSpace(part.ToolCallId))
            {
                var id = part.ToolCallId!;
                callIds.Add(id);
                if (!tools.TryGetValue(id, out var tool))
                {
                    tool = new ToolState(id, item.Sequence);
                    tools.Add(id, tool);
                }
                else if (tool.MessageSequence.HasValue)
                {
                    assertions.Add("trajectory_tool_duplicate_call");
                }

                if (tool.TurnId is not null
                    && !string.Equals(
                        tool.TurnId,
                        item.TurnId,
                        StringComparison.Ordinal))
                {
                    assertions.Add("trajectory_tool_turn_mismatch");
                }

                tool.MessageSequence ??= item.Sequence;
                tool.TurnId ??= item.TurnId;
                tool.ToolName ??= part.ToolName;
                tool.ToolVersion ??= part.ToolVersion;
                tool.Effect ??= part.ToolEffect;
                tool.ArgumentsDigest ??= part.Json.HasValue
                    ? DigestJson("tool-arguments", part.Json.Value)
                    : null;
            }
            else if (part.Type == NormalizedPartTypes.ToolResult
                     && !string.IsNullOrWhiteSpace(part.ToolCallId))
            {
                var id = part.ToolCallId!;
                resultIds.Add(id);
                if (!tools.TryGetValue(id, out var tool))
                {
                    tool = new ToolState(id, item.Sequence);
                    tools.Add(id, tool);
                    assertions.Add("trajectory_tool_orphan_result");
                }

                if (tool.ResultMessageSequence.HasValue)
                {
                    assertions.Add("trajectory_tool_duplicate_result");
                }

                if (tool.TurnId is not null
                    && !string.Equals(
                        tool.TurnId,
                        item.TurnId,
                        StringComparison.Ordinal))
                {
                    assertions.Add("trajectory_tool_turn_mismatch");
                }

                var resultDigest = part.Json.HasValue
                    ? DigestJson("tool-result", part.Json.Value)
                    : null;
                if (tool.ResultDigest is not null
                    && resultDigest is not null
                    && !string.Equals(
                        tool.ResultDigest,
                        resultDigest,
                        StringComparison.Ordinal))
                {
                    assertions.Add(
                        "trajectory_tool_result_digest_mismatch");
                }

                tool.ResultMessageSequence ??= item.Sequence;
                tool.ResultSequence ??= item.Sequence;
                tool.ResultDigest ??= resultDigest;
                tool.ResultStatus ??= TryReceiptStatus(part.Json);
            }
        }

        messages.Add(
            new RuntimeTrajectoryMessage(
                message.MessageId,
                item.TurnId,
                message.Role,
                item.Sequence,
                DigestJson(
                    "normalized-message",
                    NormalizedMessageJournalCodec.Encode(message)),
                new ReadOnlyCollection<string>(callIds),
                new ReadOnlyCollection<string>(resultIds)));
    }

    private static void ProcessToolStarted(
        RuntimeEvent item,
        IDictionary<string, ToolState> tools,
        ISet<string> assertions)
    {
        ToolInvocation invocation;
        try
        {
            invocation = ProtocolJson.DeserializeToolInvocation(
                item.Payload.GetRawText());
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            assertions.Add("trajectory_tool_invocation_invalid");
            return;
        }

        if (ProtocolValidator.Validate(invocation).Count > 0)
        {
            assertions.Add("trajectory_tool_invocation_invalid");
        }

        if (!string.Equals(invocation.RunId, item.RunId, StringComparison.Ordinal)
            || !string.Equals(
                invocation.TurnId,
                item.TurnId,
                StringComparison.Ordinal)
            || !string.Equals(
                invocation.AttemptId,
                item.AttemptId,
                StringComparison.Ordinal))
        {
            assertions.Add("trajectory_tool_invocation_identity_mismatch");
        }

        if (!tools.TryGetValue(invocation.ToolCallId, out var tool))
        {
            tool = new ToolState(invocation.ToolCallId, item.Sequence);
            tools.Add(invocation.ToolCallId, tool);
            assertions.Add("trajectory_tool_orphan_start");
        }
        else if (tool.StartedSequence.HasValue)
        {
            assertions.Add("trajectory_tool_duplicate_start");
        }

        var invocationArgumentsDigest =
            DigestJson("tool-arguments", invocation.Arguments);
        if (tool.TurnId is not null
            && !string.Equals(
                tool.TurnId,
                invocation.TurnId,
                StringComparison.Ordinal))
        {
            assertions.Add("trajectory_tool_turn_mismatch");
        }

        if (tool.ToolName is not null
            && !string.Equals(
                tool.ToolName,
                invocation.ToolName,
                StringComparison.Ordinal)
            || tool.ToolVersion is not null
            && !string.Equals(
                tool.ToolVersion,
                invocation.ToolVersion,
                StringComparison.Ordinal)
            || tool.Effect is not null
            && !string.Equals(
                tool.Effect,
                invocation.Effect,
                StringComparison.Ordinal))
        {
            assertions.Add(
                "trajectory_tool_invocation_descriptor_mismatch");
        }

        if (tool.ArgumentsDigest is not null
            && !string.Equals(
                tool.ArgumentsDigest,
                invocationArgumentsDigest,
                StringComparison.Ordinal))
        {
            assertions.Add("trajectory_tool_arguments_mismatch");
        }

        tool.StartedSequence ??= item.Sequence;
        tool.TurnId ??= invocation.TurnId;
        tool.ToolName ??= invocation.ToolName;
        tool.ToolVersion ??= invocation.ToolVersion;
        tool.Effect ??= invocation.Effect;
        tool.ArgumentsDigest ??= invocationArgumentsDigest;
    }

    private static void ProcessActionRequest(
        RuntimeEvent item,
        IDictionary<string, ActionState> actions,
        AgentRun? stableRun,
        ISet<string> assertions)
    {
        ActionRequest request;
        try
        {
            request = ProtocolJson.DeserializeActionRequest(
                item.Payload.GetRawText());
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            assertions.Add("trajectory_action_request_invalid");
            return;
        }

        if (ProtocolValidator.Validate(request).Count > 0)
        {
            assertions.Add("trajectory_action_request_invalid");
        }

        if (!string.Equals(request.RunId, item.RunId, StringComparison.Ordinal)
            || !string.Equals(
                request.TurnId,
                item.TurnId,
                StringComparison.Ordinal))
        {
            assertions.Add("trajectory_action_request_identity_mismatch");
        }

        if (stableRun is not null
            && (!string.Equals(
                    request.RunId,
                    stableRun.RunId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    request.AgentId,
                    stableRun.AgentId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    request.WorldId,
                    stableRun.WorldId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    request.DecisionKey,
                    stableRun.DecisionKey,
                    StringComparison.Ordinal)
                || !string.Equals(
                    request.BatchId,
                    stableRun.BatchId,
                    StringComparison.Ordinal)))
        {
            assertions.Add(
                "trajectory_action_request_run_identity_mismatch");
        }

        if (!actions.TryGetValue(request.OperationId, out var action))
        {
            action = new ActionState(request.OperationId, item.Sequence);
            actions.Add(request.OperationId, action);
        }
        else if (action.RequestSequence.HasValue)
        {
            assertions.Add("trajectory_action_duplicate_request");
        }

        action.RequestSequence ??= item.Sequence;
        action.TurnId ??= request.TurnId;
        action.ToolCallId ??= request.ToolCallId;
        action.ActionName ??= request.ActionName;
        action.ActionVersion ??= request.ActionVersion;
        action.ArgumentsDigest ??=
            DigestJson("tool-arguments", request.Arguments);
        action.RequestDigest ??=
            DigestJson("action-request", item.Payload);
        action.Request ??= request;
    }

    private static void ProcessActionReceipt(
        RuntimeEvent item,
        IDictionary<string, ActionState> actions,
        bool receiptIsHostEvent,
        AgentRun? stableRun,
        ISet<string> assertions)
    {
        ActionReceipt receipt;
        try
        {
            receipt = ProtocolJson.DeserializeActionReceipt(
                item.Payload.GetRawText());
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            assertions.Add("trajectory_action_receipt_invalid");
            return;
        }

        if (ProtocolValidator.Validate(receipt).Count > 0)
        {
            assertions.Add("trajectory_action_receipt_invalid");
        }

        if (!actions.TryGetValue(receipt.OperationId, out var action))
        {
            action = new ActionState(receipt.OperationId, item.Sequence);
            actions.Add(receipt.OperationId, action);
            assertions.Add("trajectory_action_orphan_receipt");
        }

        if (action.TurnId is not null
            && !string.Equals(
                action.TurnId,
                item.TurnId,
                StringComparison.Ordinal))
        {
            assertions.Add("trajectory_action_receipt_identity_mismatch");
        }

        if (action.Request is not null && stableRun is not null)
        {
            try
            {
                _ = ActionReceiptIngressValidator.ValidateAndClone(
                    action.Request,
                    receipt,
                    stableRun);
            }
            catch (Exception exception)
                when (exception is not OutOfMemoryException)
            {
                assertions.Add(
                    "trajectory_action_receipt_ingress_invalid");
            }
        }

        if (receiptIsHostEvent)
        {
            if (IsTerminalReceiptStatus(action.ReceiptStatus))
            {
                assertions.Add(
                    "trajectory_action_receipt_terminal_regression");
            }

            if (action.ReceiptRevision.HasValue
                && receipt.Revision <= action.ReceiptRevision.Value)
            {
                assertions.Add(
                    "trajectory_action_receipt_revision_invalid");
            }
            else
            {
                action.ReceiptSequence = item.Sequence;
                action.ReceiptRevision = receipt.Revision;
                action.ReceiptStatus = receipt.Status;
                action.ReceiptDigest =
                    DigestJson("action-receipt", item.Payload);
            }

            action.ReceiptCount = checked(action.ReceiptCount + 1);
            action.ReceiptDigest ??=
                DigestJson("action-receipt", item.Payload);
        }

    }

    private static void ProcessToolResult(
        RuntimeEvent item,
        IDictionary<string, ActionState> actions,
        IDictionary<string, ToolState> tools,
        AgentRun? stableRun,
        ISet<string> assertions)
    {
        ProcessActionReceipt(
            item,
            actions,
            receiptIsHostEvent: false,
            stableRun,
            assertions);
        ActionReceipt receipt;
        try
        {
            receipt = ProtocolJson.DeserializeActionReceipt(
                item.Payload.GetRawText());
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            return;
        }

        if (!actions.TryGetValue(receipt.OperationId, out var action)
            || string.IsNullOrWhiteSpace(action.ToolCallId))
        {
            assertions.Add("trajectory_tool_result_missing_request");
            return;
        }

        var receiptDigest = DigestJson("action-receipt", item.Payload);
        var kindMatchesStatus = string.Equals(
                item.Kind,
                RuntimeEventKinds.ToolFailed,
                StringComparison.Ordinal)
            ? string.Equals(
                receipt.Status,
                ReceiptStatuses.Failed,
                StringComparison.Ordinal)
            : string.Equals(
                  receipt.Status,
                  ReceiptStatuses.Succeeded,
                  StringComparison.Ordinal)
              || string.Equals(
                  receipt.Status,
                  ReceiptStatuses.Rejected,
                  StringComparison.Ordinal);
        if (!kindMatchesStatus)
        {
            assertions.Add(
                "trajectory_tool_terminal_status_mismatch");
        }

        if (!action.ReceiptSequence.HasValue)
        {
            assertions.Add("trajectory_tool_result_without_receipt");
        }
        else if (action.ReceiptRevision != receipt.Revision
                 || !string.Equals(
                     action.ReceiptStatus,
                     receipt.Status,
                     StringComparison.Ordinal)
                 || !string.Equals(
                     action.ReceiptDigest,
                     receiptDigest,
                     StringComparison.Ordinal))
        {
            assertions.Add("trajectory_tool_receipt_mismatch");
        }

        if (!tools.TryGetValue(action.ToolCallId!, out var tool))
        {
            tool = new ToolState(action.ToolCallId!, item.Sequence);
            tools.Add(action.ToolCallId!, tool);
            assertions.Add("trajectory_tool_orphan_result");
        }

        var toolResultDigest = DigestJson("tool-result", item.Payload);
        if (tool.ResultEventSequence.HasValue)
        {
            assertions.Add("trajectory_tool_duplicate_result_event");
        }

        if (tool.ResultDigest is not null
            && !string.Equals(
                tool.ResultDigest,
                toolResultDigest,
                StringComparison.Ordinal))
        {
            assertions.Add("trajectory_tool_result_digest_mismatch");
        }

        tool.ResultEventSequence ??= item.Sequence;
        tool.ResultSequence ??= item.Sequence;
        tool.ResultStatus ??= receipt.Status;
        tool.ResultDigest ??= toolResultDigest;
    }

    private static void ProcessProviderDispatch(
        RuntimeEvent item,
        IDictionary<string, ProviderState> providers,
        ISet<string> assertions)
    {
        var key = ProviderKey(item);
        if (key is null)
        {
            assertions.Add("trajectory_provider_attempt_id_missing");
            return;
        }

        if (providers.ContainsKey(key))
        {
            assertions.Add("trajectory_provider_duplicate_dispatch");
            return;
        }

        var routeMissing = string.IsNullOrWhiteSpace(item.ProviderId)
            || string.IsNullOrWhiteSpace(item.ModelId)
            || string.IsNullOrWhiteSpace(item.TransportDialect)
            || string.IsNullOrWhiteSpace(
                item.ProviderCapabilityDigest)
            || string.IsNullOrWhiteSpace(item.ProviderRouteDigest);
        if (routeMissing)
        {
            assertions.Add("trajectory_provider_route_identity_missing");
        }

        var policyValid = TryReadProviderRoutePolicy(
            item,
            out var routePolicyVersion,
            out var routePolicyDigest);
        if (!policyValid)
        {
            assertions.Add(
                "trajectory_provider_route_policy_identity_invalid");
        }

        if (!routeMissing
            && (!CanonicalJsonDigest.IsSha256(
                    item.ProviderCapabilityDigest)
                || !CanonicalJsonDigest.IsSha256(
                    item.ProviderRouteDigest)
                || !policyValid
                || !string.Equals(
                    routePolicyVersion is null
                        ? ProviderRouteIdentity.ComputeRouteDigest(
                            item.ProviderId!,
                            item.ModelId!,
                            item.TransportDialect!,
                            item.ProviderCapabilityDigest!)
                        : ProviderRouteIdentity.ComputeRouteDigest(
                            item.ProviderId!,
                            item.ModelId!,
                            item.TransportDialect!,
                            item.ProviderCapabilityDigest!,
                            routePolicyVersion,
                            routePolicyDigest!),
                    item.ProviderRouteDigest,
                    StringComparison.Ordinal)))
        {
            assertions.Add("trajectory_provider_route_identity_invalid");
        }

        providers.Add(
            key,
            new ProviderState(
                key,
                item.AttemptId,
                item.StreamAttemptId,
                item.TurnId,
                item.ProviderId,
                item.ModelId,
                item.TransportDialect,
                item.ProviderCapabilityDigest,
                item.ProviderRouteDigest,
                routePolicyVersion,
                routePolicyDigest,
                item.Sequence));
    }

    private static void ProcessProviderTerminal(
        RuntimeEvent item,
        IDictionary<string, ProviderState> providers,
        ISet<string> assertions)
    {
        var key = ProviderKey(item);
        if (key is null || !providers.TryGetValue(key, out var provider))
        {
            assertions.Add("trajectory_provider_settlement_without_dispatch");
            return;
        }

        if (provider.TerminalSequence.HasValue)
        {
            assertions.Add("trajectory_provider_duplicate_settlement");
            return;
        }

        if (!ProviderIdentityMatches(provider, item))
        {
            assertions.Add(
                "trajectory_provider_settlement_identity_mismatch");
        }

        provider.TerminalSequence = item.Sequence;
        provider.TerminalKind = item.Kind;
    }

    private static void AssignProviderUsage(
        RuntimeEvent item,
        AgentUsage usage,
        AgentUsage? previous,
        IDictionary<string, ProviderState> providers,
        ISet<string> assertions)
    {
        var key = ProviderKey(item);
        if (key is null || !providers.TryGetValue(key, out var provider))
        {
            assertions.Add("trajectory_provider_usage_without_dispatch");
            return;
        }

        if (!ProviderIdentityMatches(provider, item))
        {
            assertions.Add("trajectory_provider_usage_identity_mismatch");
        }

        if (!TryUsageDelta(
                usage.ProviderUsageSamples,
                previous?.ProviderUsageSamples ?? 0,
                out var usageSamples)
            || !TryUsageDelta(
                usage.InputTokens,
                previous?.InputTokens ?? 0,
                out var inputTokens)
            || !TryUsageDelta(
                usage.OutputTokens,
                previous?.OutputTokens ?? 0,
                out var outputTokens))
        {
            assertions.Add(
                "trajectory_provider_usage_progression_invalid");
        }
        else
        {
            provider.UsageSamples = checked(
                provider.UsageSamples + usageSamples);
            provider.InputTokens = checked(
                provider.InputTokens + inputTokens);
            provider.OutputTokens = checked(
                provider.OutputTokens + outputTokens);
        }
        if (!RuntimeTraceNumbers.TrySubtractCosts(
                usage.CostUsd,
                previous?.CostUsd ?? "0",
                out var costDelta)
            || !RuntimeTraceNumbers.TryAddCosts(
                provider.CostUsd,
                costDelta,
                out var providerCost))
        {
            assertions.Add("trajectory_provider_cost_invalid");
        }
        else
        {
            provider.CostUsd = providerCost;
        }
    }

    private static void ValidateRunCheckpoint(
        RuntimeEvent item,
        AgentRun run,
        ISet<string> assertions)
    {
        if (ProtocolValidator.Validate(run).Count > 0)
        {
            assertions.Add("trajectory_run_checkpoint_invalid");
        }

        if (!string.Equals(run.RunId, item.RunId, StringComparison.Ordinal)
            || run.RuntimeGeneration != item.RuntimeGeneration)
        {
            assertions.Add("trajectory_run_checkpoint_identity_mismatch");
        }

        if (string.Equals(
                item.Durability,
                EventDurabilities.Durable,
                StringComparison.Ordinal)
            && (item.Sequence == long.MaxValue
                || run.Revision != item.Sequence + 1))
        {
            assertions.Add("trajectory_run_revision_mismatch");
        }

    }

    private static void FinalizeTurnAssertions(
        IReadOnlyDictionary<string, TurnState> turns,
        ISet<string> assertions)
    {
        foreach (var turn in turns.Values)
        {
            if (!turn.StartedSequence.HasValue)
            {
                assertions.Add("trajectory_turn_start_missing");
            }

            if (turn.CompletedSequence.HasValue
                && (!turn.StartedSequence.HasValue
                    || turn.CompletedSequence < turn.StartedSequence))
            {
                assertions.Add("trajectory_turn_completion_invalid");
            }
        }
    }

    private static void FinalizeToolAssertions(
        IReadOnlyDictionary<string, ToolState> tools,
        ISet<string> assertions)
    {
        foreach (var tool in tools.Values)
        {
            if (!tool.MessageSequence.HasValue)
            {
                assertions.Add("trajectory_tool_call_message_missing");
            }

            if (!tool.StartedSequence.HasValue
                && !tool.ResultSequence.HasValue)
            {
                assertions.Add("trajectory_tool_start_missing");
            }

            if (!tool.ResultSequence.HasValue)
            {
                assertions.Add("trajectory_tool_result_missing");
            }
        }
    }

    private static void FinalizeActionAssertions(
        IReadOnlyDictionary<string, ActionState> actions,
        IReadOnlyDictionary<string, ToolState> tools,
        ISet<string> assertions)
    {
        foreach (var action in actions.Values)
        {
            if (!action.RequestSequence.HasValue)
            {
                assertions.Add("trajectory_action_request_missing");
            }

            if (!action.ReceiptSequence.HasValue)
            {
                assertions.Add("trajectory_action_receipt_missing");
            }

            if (string.IsNullOrWhiteSpace(action.ToolCallId)
                || !tools.ContainsKey(action.ToolCallId!))
            {
                assertions.Add("trajectory_action_tool_correlation_missing");
            }
            else
            {
                var tool = tools[action.ToolCallId!];
                if (action.TurnId is not null
                    && tool.TurnId is not null
                    && !string.Equals(
                        action.TurnId,
                        tool.TurnId,
                        StringComparison.Ordinal))
                {
                    assertions.Add(
                        "trajectory_action_tool_identity_mismatch");
                }

                if (action.ArgumentsDigest is not null
                    && tool.ArgumentsDigest is not null
                    && !string.Equals(
                        action.ArgumentsDigest,
                        tool.ArgumentsDigest,
                        StringComparison.Ordinal))
                {
                    assertions.Add(
                        "trajectory_action_tool_arguments_mismatch");
                }

                if (action.ActionName is not null
                    && tool.ToolName is not null
                    && !string.Equals(
                        action.ActionName,
                        tool.ToolName,
                        StringComparison.Ordinal)
                    || action.ActionVersion is not null
                    && tool.ToolVersion is not null
                    && !string.Equals(
                        action.ActionVersion,
                        tool.ToolVersion,
                        StringComparison.Ordinal))
                {
                    assertions.Add(
                        "trajectory_action_tool_descriptor_mismatch");
                }
            }
        }
    }

    private static void FinalizeProviderAssertions(
        IReadOnlyDictionary<string, ProviderState> providers,
        ISet<string> assertions)
    {
        foreach (var provider in providers.Values)
        {
            if (!provider.TerminalSequence.HasValue)
            {
                assertions.Add("trajectory_provider_settlement_missing");
            }
        }
    }

    private static bool IsBudgetCompliant(
        AgentBudget? budget,
        RuntimeTrajectoryUsage usage,
        string? terminalKind,
        ISet<string> assertions)
    {
        if (budget is null)
        {
            return true;
        }

        if (budget.MaxTurns < 1
            || budget.MaxDurationMs < 1
            || budget.MaxTokens < 1
            || budget.MaxActions < 0
            || usage.Turns < 0
            || usage.DurationMs < 0
            || usage.InputTokens < 0
            || usage.OutputTokens < 0
            || usage.Actions < 0
            || !RuntimeTraceNumbers.IsCanonicalCost(
                budget.MaxCostUsd,
                out _)
            || !RuntimeTraceNumbers.IsCanonicalCost(
                usage.CostUsd,
                out _))
        {
            assertions.Add("trajectory_budget_value_invalid");
            return false;
        }

        var exceeded = usage.Turns > budget.MaxTurns
            || usage.DurationMs > budget.MaxDurationMs
            || usage.TotalTokens > budget.MaxTokens
            || usage.Actions > budget.MaxActions
            || !RuntimeTraceNumbers.TryCompareCosts(
                usage.CostUsd,
                budget.MaxCostUsd,
                out var costComparison)
            || costComparison > 0;
        if (exceeded
            && !string.Equals(
                terminalKind,
                RuntimeEventKinds.RunBudgetExhausted,
                StringComparison.Ordinal))
        {
            assertions.Add("trajectory_budget_exceeded_without_terminal");
            return false;
        }

        return true;
    }

    private static RuntimeTrajectoryUsage ToUsage(AgentUsage? usage)
    {
        return usage is null
            ? new RuntimeTrajectoryUsage(
                0,
                0,
                0,
                0,
                "0",
                0,
                null,
                null,
                null,
                null,
                null,
                UsageAvailabilityStates.CostAvailable,
                0,
                false,
                0)
            : new RuntimeTrajectoryUsage(
                usage.Turns,
                usage.DurationMs,
                usage.InputTokens,
                usage.OutputTokens,
                usage.CostUsd,
                usage.ProviderUsageSamples,
                usage.CacheReadTokens,
                usage.CacheWriteTokens,
                usage.CacheMissTokens,
                usage.ReasoningTokens,
                usage.ProviderTotalTokens,
                usage.Availability,
                usage.Actions,
                usage.HasUnaccountedUsage,
                usage.UnaccountedProviderAttempts);
    }

    private static RuntimeTrajectoryToolCall ToToolCall(ToolState item)
    {
        return new RuntimeTrajectoryToolCall(
            item.ToolCallId,
            item.TurnId,
            item.ToolName,
            item.ToolVersion,
            item.Effect,
            item.MessageSequence,
            item.StartedSequence,
            item.ResultSequence,
            item.ResultStatus,
            item.ArgumentsDigest,
            item.ResultDigest);
    }

    private static RuntimeTrajectoryAction ToAction(ActionState item)
    {
        return new RuntimeTrajectoryAction(
            item.OperationId,
            item.TurnId,
            item.ToolCallId,
            item.ActionName,
            item.ActionVersion,
            item.RequestSequence,
            item.ReceiptSequence,
            item.ReceiptCount,
            item.ReceiptRevision,
            item.ReceiptStatus,
            item.ArgumentsDigest,
            item.RequestDigest,
            item.ReceiptDigest);
    }

    private static RuntimeTrajectoryProviderAttempt ToProviderAttempt(
        ProviderState item)
    {
        return new RuntimeTrajectoryProviderAttempt(
            item.AttemptKey,
            item.AttemptId,
            item.StreamAttemptId,
            item.TurnId,
            item.ProviderId,
            item.ModelId,
            item.TransportDialect,
            item.CapabilityDigest,
            item.RouteDigest,
            item.RoutePolicyVersion,
            item.RoutePolicyDigest,
            item.DispatchSequence,
            item.TerminalSequence,
            item.TerminalKind,
            item.UsageSamples,
            item.InputTokens,
            item.OutputTokens,
            item.CostUsd);
    }

    private static string ComputeTrajectoryDigest(
        IReadOnlyList<RuntimeTrajectoryEvent> events,
        IReadOnlyList<RuntimeTrajectoryTurn> turns,
        IReadOnlyList<RuntimeTrajectoryMessage> messages,
        IReadOnlyList<RuntimeTrajectoryToolCall> tools,
        IReadOnlyList<RuntimeTrajectoryAction> actions,
        IReadOnlyList<RuntimeTrajectoryProviderAttempt> providers,
        RuntimeTrajectoryUsage usage,
        bool budgetCompliant,
        IReadOnlyList<string> assertions)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "runtime-trajectory-v1");
        foreach (var item in events)
        {
            digest.Add("event.id", item.EventId);
            digest.Add("event.run", item.RunId);
            digest.Add("event.turn", item.TurnId);
            digest.Add("event.sequence", item.Sequence);
            digest.Add("event.kind", item.Kind);
            digest.Add("event.durability", item.Durability);
            digest.Add("event.generation", item.RuntimeGeneration);
            digest.Add("event.attempt", item.AttemptId);
            digest.Add("event.stream", item.StreamAttemptId);
            digest.Add("event.provider", item.ProviderId);
            digest.Add("event.model", item.ModelId);
            digest.Add("event.dialect", item.TransportDialect);
            digest.Add("event.capability", item.CapabilityDigest);
            digest.Add("event.route", item.RouteDigest);
            digest.Add("event.reason", item.ReasonCode);
            digest.Add(
                "event.timestamp",
                item.Timestamp.ToUniversalTime().ToString(
                    "O",
                    CultureInfo.InvariantCulture));
            digest.Add("event.payload", item.PayloadDigest);
            digest.Add("event.digest", item.EventDigest);
        }

        foreach (var item in turns)
        {
            digest.Add("turn.id", item.TurnId);
            digest.Add("turn.started", item.StartedSequence ?? -1);
            digest.Add("turn.completed", item.CompletedSequence ?? -1);
            digest.Add("turn.snapshot", item.SnapshotDigest);
        }

        foreach (var item in messages)
        {
            digest.Add("message.id", item.MessageId);
            digest.Add("message.turn", item.TurnId);
            digest.Add("message.role", item.Role);
            digest.Add("message.sequence", item.Sequence);
            digest.Add("message.content", item.ContentDigest);
            digest.Add("message.calls", item.ToolCallIds);
            digest.Add("message.results", item.ToolResultIds);
        }

        foreach (var item in tools)
        {
            digest.Add("tool.id", item.ToolCallId);
            digest.Add("tool.turn", item.TurnId);
            digest.Add("tool.name", item.ToolName);
            digest.Add("tool.version", item.ToolVersion);
            digest.Add("tool.effect", item.Effect);
            digest.Add("tool.message", item.MessageSequence ?? -1);
            digest.Add("tool.started", item.StartedSequence ?? -1);
            digest.Add("tool.result", item.ResultSequence ?? -1);
            digest.Add("tool.status", item.ResultStatus);
            digest.Add("tool.arguments", item.ArgumentsDigest);
            digest.Add("tool.output", item.ResultDigest);
        }

        foreach (var item in actions)
        {
            digest.Add("action.id", item.OperationId);
            digest.Add("action.turn", item.TurnId);
            digest.Add("action.tool", item.ToolCallId);
            digest.Add("action.name", item.ActionName);
            digest.Add("action.version", item.ActionVersion);
            digest.Add("action.request", item.RequestSequence ?? -1);
            digest.Add("action.receipt", item.ReceiptSequence ?? -1);
            digest.Add("action.receiptCount", item.ReceiptCount);
            digest.Add("action.receiptRevision", item.ReceiptRevision ?? -1);
            digest.Add("action.status", item.ReceiptStatus);
            digest.Add("action.argumentsDigest", item.ArgumentsDigest);
            digest.Add("action.requestDigest", item.RequestDigest);
            digest.Add("action.receiptDigest", item.ReceiptDigest);
        }

        foreach (var item in providers)
        {
            digest.Add("provider.key", item.AttemptKey);
            digest.Add("provider.attempt", item.AttemptId);
            digest.Add("provider.stream", item.StreamAttemptId);
            digest.Add("provider.turn", item.TurnId);
            digest.Add("provider.id", item.ProviderId);
            digest.Add("provider.model", item.ModelId);
            digest.Add("provider.dialect", item.TransportDialect);
            digest.Add("provider.capability", item.CapabilityDigest);
            digest.Add("provider.route", item.RouteDigest);
            digest.Add(
                "provider.routePolicyVersion",
                item.RoutePolicyVersion);
            digest.Add(
                "provider.routePolicyDigest",
                item.RoutePolicyDigest);
            digest.Add("provider.dispatch", item.DispatchSequence);
            digest.Add("provider.terminal", item.TerminalSequence ?? -1);
            digest.Add("provider.terminalKind", item.TerminalKind);
            digest.Add("provider.usageSamples", item.UsageSamples);
            digest.Add("provider.inputTokens", item.InputTokens);
            digest.Add("provider.outputTokens", item.OutputTokens);
            digest.Add("provider.costUsd", item.CostUsd);
        }

        digest.Add("usage.turns", usage.Turns);
        digest.Add("usage.durationMs", usage.DurationMs);
        digest.Add("usage.inputTokens", usage.InputTokens);
        digest.Add("usage.outputTokens", usage.OutputTokens);
        digest.Add("usage.costUsd", usage.CostUsd);
        digest.Add(
            "usage.providerUsageSamples",
            usage.ProviderUsageSamples);
        digest.Add(
            "usage.cacheReadTokens",
            usage.CacheReadTokens ?? -1);
        digest.Add(
            "usage.cacheWriteTokens",
            usage.CacheWriteTokens ?? -1);
        digest.Add(
            "usage.cacheMissTokens",
            usage.CacheMissTokens ?? -1);
        digest.Add(
            "usage.reasoningTokens",
            usage.ReasoningTokens ?? -1);
        digest.Add(
            "usage.providerTotalTokens",
            usage.ProviderTotalTokens ?? -1);
        digest.Add("usage.availability", usage.Availability);
        digest.Add("usage.actions", usage.Actions);
        digest.Add(
            "usage.unaccounted",
            usage.HasUnaccountedUsage ? "true" : "false");
        digest.Add(
            "usage.unaccountedAttempts",
            usage.UnaccountedProviderAttempts);
        digest.Add(
            "budget.compliant",
            budgetCompliant ? "true" : "false");
        digest.Add("assertions", assertions);
        return digest.Finish();
    }

    private static TurnState? GetTurn(
        IDictionary<string, TurnState> turns,
        string? turnId,
        ISet<string> assertions)
    {
        if (string.IsNullOrWhiteSpace(turnId))
        {
            assertions.Add("trajectory_turn_id_missing");
            return null;
        }

        if (!turns.TryGetValue(turnId, out var turn))
        {
            turn = new TurnState(turnId);
            turns.Add(turnId, turn);
        }

        return turn;
    }

    private static bool TryDecodeAgentRun(
        JsonElement payload,
        out AgentRun run)
    {
        run = null!;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("runId", out _)
            || !payload.TryGetProperty("budget", out _)
            || !payload.TryGetProperty("usage", out _))
        {
            return false;
        }

        try
        {
            run = ProtocolJson.DeserializeAgentRun(payload.GetRawText());
            return run.Budget is not null && run.Usage is not null;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static string? ProviderKey(RuntimeEvent item)
    {
        if (!string.IsNullOrWhiteSpace(item.StreamAttemptId))
        {
            return "stream:" + item.StreamAttemptId;
        }

        return string.IsNullOrWhiteSpace(item.AttemptId)
            ? null
            : "attempt:" + item.AttemptId;
    }

    private static bool ProviderIdentityMatches(
        ProviderState provider,
        RuntimeEvent item)
    {
        return string.Equals(
                   provider.AttemptId,
                   item.AttemptId,
                   StringComparison.Ordinal)
            && string.Equals(
                provider.StreamAttemptId,
                item.StreamAttemptId,
                StringComparison.Ordinal)
            && string.Equals(
                provider.TurnId,
                item.TurnId,
                StringComparison.Ordinal)
            && string.Equals(
                provider.ProviderId,
                item.ProviderId,
                StringComparison.Ordinal)
            && MatchesOptional(provider.ModelId, item.ModelId)
            && MatchesOptional(
                provider.TransportDialect,
                item.TransportDialect);
    }

    private static bool MatchesOptional(
        string? expected,
        string? actual)
    {
        return expected is null
            || actual is null
            || string.Equals(expected, actual, StringComparison.Ordinal);
    }

    private static bool IsAllowedAfterTerminal(string kind)
    {
        return kind is RuntimeEventKinds.BudgetUpdated
            or RuntimeEventKinds.ProviderDispatchKnownZero
            or RuntimeEventKinds.ProviderUsageUncertain
            or RuntimeEventKinds.ProviderResultCommitted
            or RuntimeEventKinds.ProviderResultDiscarded
            or RuntimeEventKinds.MemoryCommitCompleted;
    }

    private static string? TryReceiptStatus(JsonElement? value)
    {
        if (!value.HasValue
            || value.Value.ValueKind != JsonValueKind.Object
            || !value.Value.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return status.GetString();
    }

    private static bool IsTerminalReceiptStatus(string? status)
    {
        return status is ReceiptStatuses.Succeeded
            or ReceiptStatuses.Rejected
            or ReceiptStatuses.Failed;
    }

    private static bool TryUsageDelta(
        int current,
        int previous,
        out long delta)
    {
        delta = (long)current - previous;
        return delta >= 0;
    }

    private static bool TryReadProviderRoutePolicy(
        RuntimeEvent item,
        out string? policyVersion,
        out string? policyDigest)
    {
        policyVersion = null;
        policyDigest = null;
        var hasVersion = item.Extensions.TryGetValue(
            ProviderRouteJournalExtensions.PolicyVersion,
            out var versionElement);
        var hasDigest = item.Extensions.TryGetValue(
            ProviderRouteJournalExtensions.PolicyDigest,
            out var digestElement);
        if (hasVersion != hasDigest)
        {
            return false;
        }

        if (!hasVersion)
        {
            return true;
        }

        if (versionElement.ValueKind != JsonValueKind.String
            || digestElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        try
        {
            policyVersion = RuntimeGuard.RequiredUtf8(
                versionElement.GetString(),
                128,
                ProviderRouteJournalExtensions.PolicyVersion);
        }
        catch (ArgumentException)
        {
            return false;
        }

        policyDigest = digestElement.GetString();
        return CanonicalJsonDigest.IsSha256(policyDigest);
    }

    private static string DigestJson(string type, JsonElement value)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", type);
        digest.Add("value", value);
        return digest.Finish();
    }

    private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values)
    {
        return new ReadOnlyCollection<T>(values.ToArray());
    }

    private static IReadOnlyList<string> ReadOnlySorted(
        IEnumerable<string> values)
    {
        return new ReadOnlyCollection<string>(
            values.OrderBy(item => item, StringComparer.Ordinal).ToArray());
    }

    private sealed class TurnState
    {
        private long? _startedSequence;
        private long? _completedSequence;

        public TurnState(string turnId)
        {
            TurnId = turnId;
        }

        public string TurnId { get; }

        public long FirstSequence { get; set; } = long.MaxValue;

        public long? StartedSequence
        {
            get => _startedSequence;
            set
            {
                _startedSequence = value;
                if (value.HasValue)
                {
                    FirstSequence = Math.Min(FirstSequence, value.Value);
                }
            }
        }

        public long? CompletedSequence
        {
            get => _completedSequence;
            set
            {
                _completedSequence = value;
                if (value.HasValue)
                {
                    FirstSequence = Math.Min(FirstSequence, value.Value);
                }
            }
        }

        public string? SnapshotDigest { get; set; }
    }

    private sealed class ToolState
    {
        public ToolState(string toolCallId, long firstSequence)
        {
            ToolCallId = toolCallId;
            FirstSequence = firstSequence;
        }

        public string ToolCallId { get; }

        public long FirstSequence { get; }

        public string? TurnId { get; set; }

        public string? ToolName { get; set; }

        public string? ToolVersion { get; set; }

        public string? Effect { get; set; }

        public long? MessageSequence { get; set; }

        public long? StartedSequence { get; set; }

        public long? ResultSequence { get; set; }

        public long? ResultMessageSequence { get; set; }

        public long? ResultEventSequence { get; set; }

        public string? ResultStatus { get; set; }

        public string? ArgumentsDigest { get; set; }

        public string? ResultDigest { get; set; }
    }

    private sealed class ActionState
    {
        public ActionState(string operationId, long firstSequence)
        {
            OperationId = operationId;
            FirstSequence = firstSequence;
        }

        public string OperationId { get; }

        public long FirstSequence { get; }

        public string? TurnId { get; set; }

        public string? ToolCallId { get; set; }

        public string? ActionName { get; set; }

        public string? ActionVersion { get; set; }

        public string? ArgumentsDigest { get; set; }

        public long? RequestSequence { get; set; }

        public long? ReceiptSequence { get; set; }

        public int ReceiptCount { get; set; }

        public long? ReceiptRevision { get; set; }

        public string? ReceiptStatus { get; set; }

        public string? RequestDigest { get; set; }

        public string? ReceiptDigest { get; set; }

        public ActionRequest? Request { get; set; }

    }

    private sealed class ProviderState
    {
        public ProviderState(
            string attemptKey,
            string? attemptId,
            string? streamAttemptId,
            string? turnId,
            string? providerId,
            string? modelId,
            string? transportDialect,
            string? capabilityDigest,
            string? routeDigest,
            string? routePolicyVersion,
            string? routePolicyDigest,
            long dispatchSequence)
        {
            AttemptKey = attemptKey;
            AttemptId = attemptId;
            StreamAttemptId = streamAttemptId;
            TurnId = turnId;
            ProviderId = providerId;
            ModelId = modelId;
            TransportDialect = transportDialect;
            CapabilityDigest = capabilityDigest;
            RouteDigest = routeDigest;
            RoutePolicyVersion = routePolicyVersion;
            RoutePolicyDigest = routePolicyDigest;
            DispatchSequence = dispatchSequence;
        }

        public string AttemptKey { get; }

        public string? AttemptId { get; }

        public string? StreamAttemptId { get; }

        public string? TurnId { get; }

        public string? ProviderId { get; }

        public string? ModelId { get; }

        public string? TransportDialect { get; }

        public string? CapabilityDigest { get; }

        public string? RouteDigest { get; }

        public string? RoutePolicyVersion { get; }

        public string? RoutePolicyDigest { get; }

        public long DispatchSequence { get; }

        public long? TerminalSequence { get; set; }

        public string? TerminalKind { get; set; }

        public long UsageSamples { get; set; }

        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }

        public string CostUsd { get; set; } = "0";
    }
}

internal static class RuntimeTraceNumbers
{
    public static bool IsCanonicalCost(
        string? value,
        out int decimalIndex)
    {
        decimalIndex = -1;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var integerLength = value.Length;
        var dot = value.IndexOf('.');
        if (dot >= 0)
        {
            if (dot == 0
                || dot == value.Length - 1
                || value.IndexOf('.', dot + 1) >= 0)
            {
                return false;
            }

            integerLength = dot;
            decimalIndex = dot;
        }

        if (value[0] == '0')
        {
            if (integerLength != 1)
            {
                return false;
            }
        }
        else if (value[0] is < '1' or > '9')
        {
            return false;
        }

        for (var index = 1; index < integerLength; index++)
        {
            if (value[index] is < '0' or > '9')
            {
                return false;
            }
        }

        for (var index = dot < 0 ? value.Length : dot + 1;
             index < value.Length;
             index++)
        {
            if (value[index] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryCompareCosts(
        string? left,
        string? right,
        out int comparison)
    {
        comparison = 0;
        if (!TryParts(left, out var leftParts)
            || !TryParts(right, out var rightParts))
        {
            return false;
        }

        if (leftParts.IntegerLength != rightParts.IntegerLength)
        {
            comparison = leftParts.IntegerLength.CompareTo(
                rightParts.IntegerLength);
            return true;
        }

        comparison = string.CompareOrdinal(
            left,
            0,
            right,
            0,
            leftParts.IntegerLength);
        if (comparison != 0)
        {
            return true;
        }

        var fractionalLength = Math.Max(
            leftParts.FractionLength,
            rightParts.FractionLength);
        for (var index = 0; index < fractionalLength; index++)
        {
            var leftDigit = index < leftParts.FractionLength
                ? left![leftParts.IntegerLength + index + 1]
                : '0';
            var rightDigit = index < rightParts.FractionLength
                ? right![rightParts.IntegerLength + index + 1]
                : '0';
            if (leftDigit != rightDigit)
            {
                comparison = leftDigit.CompareTo(rightDigit);
                return true;
            }
        }

        return true;
    }

    public static bool TryAddCosts(
        string? left,
        string? right,
        out string result)
    {
        result = "0";
        if (!TryParts(left, out var leftParts)
            || !TryParts(right, out var rightParts))
        {
            return false;
        }

        var scale = Math.Max(
            leftParts.FractionLength,
            rightParts.FractionLength);
        var leftDigits = ScaledDigits(left!, leftParts, scale);
        var rightDigits = ScaledDigits(right!, rightParts, scale);
        var maximum = Math.Max(leftDigits.Length, rightDigits.Length);
        var output = new char[checked(maximum + 1)];
        var carry = 0;
        for (var offset = 0; offset < maximum; offset++)
        {
            var leftIndex = leftDigits.Length - offset - 1;
            var rightIndex = rightDigits.Length - offset - 1;
            var sum = carry
                + (leftIndex >= 0 ? leftDigits[leftIndex] - '0' : 0)
                + (rightIndex >= 0 ? rightDigits[rightIndex] - '0' : 0);
            output[maximum - offset] = (char)('0' + sum % 10);
            carry = sum / 10;
        }

        output[0] = (char)('0' + carry);
        result = FormatScaled(
            output,
            carry == 0 ? 1 : 0,
            scale);
        return true;
    }

    public static bool TrySubtractCosts(
        string? left,
        string? right,
        out string result)
    {
        result = "0";
        if (!TryCompareCosts(left, right, out var comparison)
            || comparison < 0
            || !TryParts(left, out var leftParts)
            || !TryParts(right, out var rightParts))
        {
            return false;
        }

        var scale = Math.Max(
            leftParts.FractionLength,
            rightParts.FractionLength);
        var leftDigits = ScaledDigits(left!, leftParts, scale);
        var rightDigits = ScaledDigits(right!, rightParts, scale);
        var output = new char[leftDigits.Length];
        var borrow = 0;
        for (var offset = 0; offset < leftDigits.Length; offset++)
        {
            var leftIndex = leftDigits.Length - offset - 1;
            var rightIndex = rightDigits.Length - offset - 1;
            var difference = leftDigits[leftIndex] - '0' - borrow
                - (rightIndex >= 0 ? rightDigits[rightIndex] - '0' : 0);
            if (difference < 0)
            {
                difference += 10;
                borrow = 1;
            }
            else
            {
                borrow = 0;
            }

            output[leftIndex] = (char)('0' + difference);
        }

        var start = 0;
        while (start < output.Length - 1 && output[start] == '0')
        {
            start++;
        }

        result = FormatScaled(output, start, scale);
        return true;
    }

    private static bool TryParts(
        string? value,
        out CostParts parts)
    {
        parts = default;
        if (!IsCanonicalCost(value, out var decimalIndex))
        {
            return false;
        }

        parts = decimalIndex < 0
            ? new CostParts(value!.Length, 0)
            : new CostParts(
                decimalIndex,
                value!.Length - decimalIndex - 1);
        return true;
    }

    private static string ScaledDigits(
        string value,
        CostParts parts,
        int scale)
    {
        var result = new char[checked(parts.IntegerLength + scale)];
        value.AsSpan(0, parts.IntegerLength).CopyTo(result);
        if (parts.FractionLength > 0)
        {
            value.AsSpan(
                    parts.IntegerLength + 1,
                    parts.FractionLength)
                .CopyTo(result.AsSpan(parts.IntegerLength));
        }

        for (var index = parts.IntegerLength + parts.FractionLength;
             index < result.Length;
             index++)
        {
            result[index] = '0';
        }

        return new string(result);
    }

    private static string FormatScaled(
        char[] digits,
        int start,
        int scale)
    {
        while (start < digits.Length - 1 && digits[start] == '0')
        {
            start++;
        }

        var digitCount = digits.Length - start;
        if (scale == 0)
        {
            return new string(digits, start, digitCount);
        }

        var minimumDigits = checked(scale + 1);
        string scaled;
        if (digitCount < minimumDigits)
        {
            scaled = new string('0', minimumDigits - digitCount)
                + new string(digits, start, digitCount);
        }
        else
        {
            scaled = new string(digits, start, digitCount);
        }

        var integerLength = scaled.Length - scale;
        var fractionEnd = scaled.Length;
        while (fractionEnd > integerLength
               && scaled[fractionEnd - 1] == '0')
        {
            fractionEnd--;
        }

        var integer = scaled.Substring(0, integerLength);
        if (fractionEnd == integerLength)
        {
            return integer;
        }

        return integer
            + "."
            + scaled.Substring(
                integerLength,
                fractionEnd - integerLength);
    }

    private readonly struct CostParts
    {
        public CostParts(int integerLength, int fractionLength)
        {
            IntegerLength = integerLength;
            FractionLength = fractionLength;
        }

        public int IntegerLength { get; }

        public int FractionLength { get; }
    }
}
