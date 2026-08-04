using System.Buffers;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class RuntimeTraceExportOptions
{
    public int MaxEvents { get; set; } = 10_000;

    public int MaxUtf8Bytes { get; set; } = 16 * 1_048_576;

    public int MaxJsonDepth { get; set; } = 32;

    public int MaxJsonNodesPerEvent { get; set; } = 65_536;

    internal RuntimeTraceExportOptions Snapshot()
    {
        if (MaxEvents is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEvents));
        }

        if (MaxUtf8Bytes is < 1_024 or > 512 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxUtf8Bytes));
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

        return new RuntimeTraceExportOptions
        {
            MaxEvents = MaxEvents,
            MaxUtf8Bytes = MaxUtf8Bytes,
            MaxJsonDepth = MaxJsonDepth,
            MaxJsonNodesPerEvent = MaxJsonNodesPerEvent
        };
    }
}

public sealed class RuntimeTraceExport
{
    internal RuntimeTraceExport(
        string jsonLines,
        int eventCount,
        int redactedValueCount,
        string digest)
    {
        JsonLines = jsonLines;
        EventCount = eventCount;
        RedactedValueCount = redactedValueCount;
        Digest = digest;
    }

    public string JsonLines { get; }

    public int EventCount { get; }

    public int RedactedValueCount { get; }

    public string Digest { get; }
}

public sealed class RuntimeTraceExporter
{
    private static readonly TimeSpan CredentialPatternTimeout =
        TimeSpan.FromMilliseconds(100);

    private static readonly string[] SensitiveNames =
    {
        "authorization",
        "credential",
        "password",
        "secret",
        "cookie",
        "api_key",
        "apikey"
    };

    private static readonly Regex[] CredentialPatterns =
    {
        CredentialPattern(
            "-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
        CredentialPattern(
            @"(?<![a-z0-9])sk-[a-z0-9_-]{20,}"),
        CredentialPattern(
            @"(?<![a-z0-9])github_pat_[a-z0-9_]{20,}"),
        CredentialPattern(
            @"(?<![a-z0-9])gh[pousr]_[a-z0-9]{20,}"),
        CredentialPattern(
            @"(?<![a-z0-9])(?:AKIA|ASIA)[0-9A-Z]{16}(?![0-9A-Z])"),
        CredentialPattern(
            @"(?<![a-z0-9])AIza[0-9A-Za-z_-]{30,}"),
        CredentialPattern(
            @"(?<![a-z0-9_-])eyJ[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}"),
        CredentialPattern(
            @"\bBearer\s+[a-z0-9._~+/-]{20,}={0,2}")
    };

    private readonly RuntimeTraceExportOptions _options;

    public RuntimeTraceExporter(RuntimeTraceExportOptions? options = null)
    {
        _options = (options ?? new RuntimeTraceExportOptions()).Snapshot();
    }

    public RuntimeTraceExport Export(IEnumerable<RuntimeEvent> events)
    {
        if (events is null)
        {
            throw new ArgumentNullException(nameof(events));
        }

        var output = new StringBuilder();
        var eventCount = 0;
        var bytes = 0;
        var redacted = 0;
        foreach (var runtimeEvent in events)
        {
            if (runtimeEvent is null)
            {
                throw new ArgumentException(
                    "Runtime trace events cannot contain null entries.",
                    nameof(events));
            }

            if (eventCount >= _options.MaxEvents)
            {
                throw new RuntimeContentLimitException(
                    nameof(events),
                    "trace_event_count_exceeded",
                    "The runtime trace contains too many events.");
            }

            Preflight(runtimeEvent);
            ProtocolValidator.EnsureValid(runtimeEvent);
            var sanitized = Sanitize(runtimeEvent, ref redacted);
            ProtocolValidator.EnsureValid(sanitized);
            var line = ProtocolJson.Serialize(sanitized);
            var lineBytes = checked(
                Encoding.UTF8.GetByteCount(line) + 1);
            if (checked(bytes + lineBytes) > _options.MaxUtf8Bytes)
            {
                throw new RuntimeContentLimitException(
                    nameof(events),
                    "trace_bytes_exceeded",
                    "The runtime trace exceeds its byte limit.");
            }

            output.Append(line);
            output.Append('\n');
            bytes += lineBytes;
            eventCount++;
        }

        var text = output.ToString();
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "redacted-runtime-trace");
        digest.Add("eventCount", eventCount);
        digest.Add("jsonLines", text);
        return new RuntimeTraceExport(
            text,
            eventCount,
            redacted,
            digest.Finish());
    }

    private RuntimeEvent Sanitize(
        RuntimeEvent source,
        ref int redacted)
    {
        return new RuntimeEvent
        {
            ProtocolVersion = source.ProtocolVersion,
            SchemaVersion = source.SchemaVersion,
            Extensions = SanitizeExtensions(source.Extensions, ref redacted),
            EventId = SanitizeId(
                source.EventId,
                nameof(RuntimeEvent.EventId),
                ref redacted)!,
            RunId = SanitizeId(
                source.RunId,
                nameof(RuntimeEvent.RunId),
                ref redacted),
            TurnId = SanitizeId(
                source.TurnId,
                nameof(RuntimeEvent.TurnId),
                ref redacted),
            Sequence = source.Sequence,
            Kind = source.Kind,
            Durability = source.Durability,
            RuntimeGeneration = source.RuntimeGeneration,
            AttemptId = SanitizeId(
                source.AttemptId,
                nameof(RuntimeEvent.AttemptId),
                ref redacted),
            StreamAttemptId = SanitizeId(
                source.StreamAttemptId,
                nameof(RuntimeEvent.StreamAttemptId),
                ref redacted),
            ProviderId = SanitizeString(source.ProviderId, ref redacted),
            ModelId = SanitizeString(source.ModelId, ref redacted),
            TransportDialect = SanitizeString(
                source.TransportDialect,
                ref redacted),
            ProviderCapabilityDigest = SanitizeString(
                source.ProviderCapabilityDigest,
                ref redacted),
            ProviderRouteDigest = SanitizeString(
                source.ProviderRouteDigest,
                ref redacted),
            ReasonCode = SanitizeString(source.ReasonCode, ref redacted),
            Timestamp = source.Timestamp,
            Payload = SanitizePayload(source, ref redacted)
        };
    }

    private JsonElement SanitizePayload(
        RuntimeEvent source,
        ref int redacted)
    {
        if (string.Equals(
                source.Kind,
                RuntimeEventKinds.TranscriptMessage,
                StringComparison.Ordinal))
        {
            try
            {
                var message = NormalizedMessageJournalCodec.Decode(
                    source.Payload);
                var before = message.Parts.Count;
                message.Parts = message.Parts
                    .Where(
                        part => !string.Equals(
                            part.Type,
                            NormalizedPartTypes.Reasoning,
                            StringComparison.Ordinal))
                    .ToList();
                redacted = checked(redacted + before - message.Parts.Count);
                return SanitizeJson(
                    NormalizedMessageJournalCodec.Encode(message),
                    propertyName: null,
                    depth: 0,
                    ref redacted);
            }
            catch (Exception exception)
                when (exception is not OutOfMemoryException
                      and not StackOverflowException)
            {
                redacted++;
                return ProtocolJson.ParseElement(
                    "\"[REDACTED:INVALID_TRANSCRIPT]\"");
            }
        }

        return SanitizeJson(
            source.Payload,
            propertyName: null,
            depth: 0,
            ref redacted);
    }

    private void Preflight(RuntimeEvent source)
    {
        var nodes = 0L;
        var bytes = 0L;
        try
        {
            if (source.Extensions is null
                || source.Extensions.Count > 4_096)
            {
                throw TraceValueLimit();
            }

            // Charge every metadata string before redaction attempts. This
            // prevents regex, URI parsing, or serialization from becoming the
            // first operation to discover an oversized event.
            AddMetadataString(source.ProtocolVersion, ref bytes);
            AddMetadataString(source.SchemaVersion, ref bytes);
            AddMetadataString(source.EventId, ref bytes);
            AddMetadataString(source.RunId, ref bytes);
            AddMetadataString(source.TurnId, ref bytes);
            AddMetadataString(source.Kind, ref bytes);
            AddMetadataString(source.Durability, ref bytes);
            AddMetadataString(source.AttemptId, ref bytes);
            AddMetadataString(source.StreamAttemptId, ref bytes);
            AddMetadataString(source.ProviderId, ref bytes);
            AddMetadataString(source.ModelId, ref bytes);
            AddMetadataString(source.TransportDialect, ref bytes);
            AddMetadataString(
                source.ProviderCapabilityDigest,
                ref bytes);
            AddMetadataString(source.ProviderRouteDigest, ref bytes);
            AddMetadataString(source.ReasonCode, ref bytes);
            foreach (var extension in source.Extensions)
            {
                AddMetadataString(extension.Key, ref bytes);
            }

            var payload = MeasureJsonValue(
                source.Payload,
                nodes,
                bytes);
            nodes += payload.Nodes;
            bytes += payload.Utf8Bytes;
            EnsureEventValueBudget(nodes, bytes);
            foreach (var extension in source.Extensions)
            {
                var measurement = MeasureJsonValue(
                    extension.Value,
                    nodes,
                    bytes);
                nodes += measurement.Nodes;
                bytes += measurement.Utf8Bytes;
                EnsureEventValueBudget(nodes, bytes);
            }
        }
        catch (RuntimeContentLimitException)
        {
            throw TraceValueLimit();
        }
        catch (OverflowException)
        {
            throw TraceValueLimit();
        }
    }

    private void AddMetadataString(string? value, ref long bytes)
    {
        if (value is null)
        {
            return;
        }

        var remaining = _options.MaxUtf8Bytes - bytes;
        if (remaining < 2 || value.Length > remaining - 2)
        {
            throw TraceValueLimit();
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
                throw TraceValueLimit();
            }

            bytes += bytesWritten;
            remaining -= bytesWritten;
            source = source[charsConsumed..];
            if (status == OperationStatus.Done)
            {
                break;
            }

            if (status != OperationStatus.DestinationTooSmall
                || charsConsumed == 0 && bytesWritten == 0)
            {
                throw TraceValueLimit();
            }
        }

        if (remaining < 2)
        {
            throw TraceValueLimit();
        }

        bytes += 2;
    }

    private JsonValueMeasurement MeasureJsonValue(
        JsonElement value,
        long consumedNodes,
        long consumedBytes)
    {
        var remainingNodes =
            _options.MaxJsonNodesPerEvent - consumedNodes;
        var remainingBytes = _options.MaxUtf8Bytes - consumedBytes;
        if (remainingNodes < 1 || remainingBytes < 1)
        {
            throw TraceValueLimit();
        }

        var limits = new JsonValueLimits(
            maxUtf8Bytes: checked((int)remainingBytes),
            maxDepth: _options.MaxJsonDepth,
            maxNodes: checked((int)remainingNodes),
            maxStringUtf8Bytes: checked((int)remainingBytes),
            maxContainerItems: checked((int)remainingNodes));
        return JsonValueInspector.ValidateAndMeasureDetailed(
            value,
            limits,
            "events");
    }

    private void EnsureEventValueBudget(long nodes, long bytes)
    {
        if (nodes > _options.MaxJsonNodesPerEvent
            || bytes > _options.MaxUtf8Bytes)
        {
            throw TraceValueLimit();
        }
    }

    private static RuntimeContentLimitException TraceValueLimit()
    {
        return new RuntimeContentLimitException(
            "events",
            "trace_event_value_exceeded",
            "A runtime trace event exceeds its JSON value limit.");
    }

    private static string? SanitizeString(
        string? value,
        ref int redacted)
    {
        if (!LooksSensitive(value))
        {
            return value;
        }

        redacted++;
        return "[REDACTED]";
    }

    private static string? SanitizeId(
        string? value,
        string field,
        ref int redacted)
    {
        if (!LooksSensitive(value))
        {
            return value;
        }

        redacted++;
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "redacted-runtime-event-id");
        digest.Add("field", field);
        digest.Add("value", value);
        return "redacted:sha256:" + digest.Finish();
    }

    private Dictionary<string, JsonElement> SanitizeExtensions(
        IReadOnlyDictionary<string, JsonElement> extensions,
        ref int redacted)
    {
        var result = new Dictionary<string, JsonElement>(
            StringComparer.Ordinal);
        var reservedNames = new HashSet<string>(
            extensions.Keys.Where(
                key => !ShouldRedactPropertyName(key)),
            StringComparer.Ordinal);
        var redactedKeyOrdinal = 0;
        foreach (var pair in extensions)
        {
            if (ShouldRedactPropertyName(pair.Key))
            {
                redacted++;
                result[NextRedactedPropertyName(
                    reservedNames,
                    ref redactedKeyOrdinal)] =
                    ProtocolJson.ParseElement("\"[REDACTED]\"");
            }
            else
            {
                result[pair.Key] = SanitizeJson(
                    pair.Value,
                    pair.Key,
                    depth: 0,
                    ref redacted);
            }
        }

        return result;
    }

    private JsonElement SanitizeJson(
        JsonElement value,
        string? propertyName,
        int depth,
        ref int redacted)
    {
        if (depth > _options.MaxJsonDepth)
        {
            redacted++;
            return ProtocolJson.ParseElement(
                "\"[REDACTED:DEPTH_LIMIT]\"");
        }

        if (IsSensitiveName(propertyName)
            || value.ValueKind == JsonValueKind.String
            && LooksSensitive(value.GetString()))
        {
            redacted++;
            return ProtocolJson.ParseElement("\"[REDACTED]\"");
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteSanitized(
                writer,
                value,
                depth,
                ref redacted);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private void WriteSanitized(
        Utf8JsonWriter writer,
        JsonElement value,
        int depth,
        ref int redacted)
    {
        if (depth > _options.MaxJsonDepth)
        {
            redacted++;
            writer.WriteStringValue("[REDACTED:DEPTH_LIMIT]");
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var reservedNames = new HashSet<string>(
                    value.EnumerateObject()
                        .Where(
                            property => !ShouldRedactPropertyName(
                                property.Name))
                        .Select(property => property.Name),
                    StringComparer.Ordinal);
                var redactedKeyOrdinal = 0;
                foreach (var property in value.EnumerateObject())
                {
                    if (ShouldRedactPropertyName(property.Name))
                    {
                        redacted++;
                        writer.WritePropertyName(
                            NextRedactedPropertyName(
                                reservedNames,
                                ref redactedKeyOrdinal));
                        writer.WriteStringValue("[REDACTED]");
                    }
                    else
                    {
                        writer.WritePropertyName(property.Name);
                        if (property.Value.ValueKind
                                == JsonValueKind.String
                            && LooksSensitive(
                                property.Value.GetString()))
                        {
                            redacted++;
                            writer.WriteStringValue("[REDACTED]");
                        }
                        else
                        {
                            WriteSanitized(
                                writer,
                                property.Value,
                                depth + 1,
                                ref redacted);
                        }
                    }
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteSanitized(writer, item, depth + 1, ref redacted);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                if (LooksSensitive(value.GetString()))
                {
                    redacted++;
                    writer.WriteStringValue("[REDACTED]");
                }
                else
                {
                    writer.WriteStringValue(value.GetString());
                }
                break;
            case JsonValueKind.Number:
                value.WriteTo(writer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
        }
    }

    private static bool ShouldRedactPropertyName(string name)
    {
        return IsSensitiveName(name) || LooksSensitive(name);
    }

    private static string NextRedactedPropertyName(
        ISet<string> reservedNames,
        ref int ordinal)
    {
        while (true)
        {
            var candidate = "[REDACTED_KEY_"
                            + ordinal.ToString(
                                System.Globalization.CultureInfo.InvariantCulture)
                            + "]";
            ordinal = checked(ordinal + 1);
            if (reservedNames.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static bool IsSensitiveName(string? name)
    {
        if (name is null)
        {
            return false;
        }

        var normalized = name
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal);
        return normalized.EndsWith(
                   "token",
                   StringComparison.OrdinalIgnoreCase)
               || SensitiveNames.Any(
                   marker => normalized.IndexOf(
                       marker.Replace(
                           "_",
                           string.Empty,
                           StringComparison.Ordinal),
                       StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static bool LooksSensitive(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        try
        {
            if (value.StartsWith(
                       "Bearer ",
                       StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("sk-", StringComparison.Ordinal)
                || CredentialPatterns.Any(pattern => pattern.IsMatch(value)))
            {
                return true;
            }

            return Uri.TryCreate(value, UriKind.Absolute, out var uri)
                   && (!string.IsNullOrEmpty(uri.UserInfo)
                       || UriQueryHasSensitiveName(uri));
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
        catch (UriFormatException)
        {
            return true;
        }
    }

    private static bool UriQueryHasSensitiveName(Uri uri)
    {
        var query = uri.Query;
        if (query.Length <= 1)
        {
            return false;
        }

        foreach (var segment in query.AsSpan(1).ToString().Split('&'))
        {
            var separator = segment.IndexOf('=');
            var rawName = separator < 0
                ? segment
                : segment.Substring(0, separator);
            if (rawName.Length > 1_024)
            {
                return true;
            }

            var name = Uri.UnescapeDataString(
                rawName.Replace("+", " ", StringComparison.Ordinal));
            if (IsSensitiveName(name))
            {
                return true;
            }
        }

        return false;
    }

    private static Regex CredentialPattern(string pattern)
    {
        return new Regex(
            pattern,
            RegexOptions.Compiled
            | RegexOptions.CultureInvariant
            | RegexOptions.IgnoreCase,
            CredentialPatternTimeout);
    }
}

public sealed class RuntimeRunProjection
{
    internal RuntimeRunProjection(
        string? runId,
        int eventCount,
        long lastSequence,
        string? terminalKind,
        int turns,
        int toolCalls,
        int actionRequests,
        int providerDispatches,
        IReadOnlyList<string> anomalyCodes)
    {
        RunId = runId;
        EventCount = eventCount;
        LastSequence = lastSequence;
        TerminalKind = terminalKind;
        Turns = turns;
        ToolCalls = toolCalls;
        ActionRequests = actionRequests;
        ProviderDispatches = providerDispatches;
        AnomalyCodes = anomalyCodes;
    }

    public string? RunId { get; }

    public int EventCount { get; }

    public long LastSequence { get; }

    public string? TerminalKind { get; }

    public int Turns { get; }

    public int ToolCalls { get; }

    public int ActionRequests { get; }

    public int ProviderDispatches { get; }

    public IReadOnlyList<string> AnomalyCodes { get; }
}

public sealed class RuntimeJournalProjector
{
    private readonly RuntimeTraceAnalysisOptions _options;

    public RuntimeJournalProjector(
        RuntimeTraceAnalysisOptions? options = null)
    {
        _options = (options ?? new RuntimeTraceAnalysisOptions()).Snapshot();
    }

    public RuntimeRunProjection Project(
        IEnumerable<RuntimeEvent> events)
    {
        return new RuntimeTraceAnalyzer(_options).Analyze(events).Projection;
    }
}

public sealed class RuntimeScenarioExpectation
{
    public IReadOnlyList<string> RequiredEventKinds { get; set; } =
        Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenEventKinds { get; set; } =
        Array.Empty<string>();

    public string? TerminalKind { get; set; }

    public int? MaximumTurns { get; set; }

    public int? MaximumActionRequests { get; set; }

    public int? MaximumToolCalls { get; set; }

    public int? MaximumProviderAttempts { get; set; }

    public int? MaximumInputTokens { get; set; }

    public int? MaximumOutputTokens { get; set; }

    public int? MaximumTotalTokens { get; set; }

    public string? MaximumCostUsd { get; set; }

    public string? ExpectedTrajectoryDigest { get; set; }

    public bool RequireValidReplay { get; set; }

    public bool RequireSettledUsage { get; set; }

    public bool RequireBudgetCompliance { get; set; }
}

public sealed class RuntimeScenarioEvaluation
{
    internal RuntimeScenarioEvaluation(
        bool passed,
        IReadOnlyList<string> failureCodes,
        RuntimeTraceAnalysis analysis)
    {
        Passed = passed;
        FailureCodes = failureCodes;
        Analysis = analysis;
    }

    public bool Passed { get; }

    public IReadOnlyList<string> FailureCodes { get; }

    public RuntimeTraceAnalysis Analysis { get; }

    public RuntimeRunProjection Projection => Analysis.Projection;
}

public sealed class RuntimeScenarioEvaluator
{
    private readonly RuntimeTraceAnalysisOptions _options;

    public RuntimeScenarioEvaluator(
        RuntimeTraceAnalysisOptions? options = null)
    {
        _options = (options ?? new RuntimeTraceAnalysisOptions()).Snapshot();
    }

    public RuntimeScenarioEvaluation Evaluate(
        IEnumerable<RuntimeEvent> events,
        RuntimeScenarioExpectation expectation)
    {
        if (expectation is null)
        {
            throw new ArgumentNullException(nameof(expectation));
        }

        var analysis = new RuntimeTraceAnalyzer(_options).Analyze(events);
        return Evaluate(analysis, expectation);
    }

    public RuntimeScenarioEvaluation Evaluate(
        RuntimeTraceAnalysis analysis,
        RuntimeScenarioExpectation expectation)
    {
        if (analysis is null)
        {
            throw new ArgumentNullException(nameof(analysis));
        }

        if (expectation is null)
        {
            throw new ArgumentNullException(nameof(expectation));
        }

        var requiredKinds = RuntimeInputGuard.CopyBounded(
            expectation.RequiredEventKinds,
            256,
            SnapshotEventKind,
            nameof(expectation),
            "scenario_required_event_kinds_exceeded");
        var forbiddenKinds = RuntimeInputGuard.CopyBounded(
            expectation.ForbiddenEventKinds,
            256,
            SnapshotEventKind,
            nameof(expectation),
            "scenario_forbidden_event_kinds_exceeded");
        EnsureOptionalBounded(
            expectation.TerminalKind,
            512,
            "scenario_terminal_kind_invalid");
        EnsureOptionalBounded(
            expectation.MaximumCostUsd,
            65_536,
            "scenario_cost_value_invalid");
        EnsureOptionalBounded(
            expectation.ExpectedTrajectoryDigest,
            1_024,
            "scenario_trajectory_digest_invalid");
        var projection = analysis.Projection;
        var kinds = analysis.EventKinds;
        var failures = new HashSet<string>(
            projection.AnomalyCodes,
            StringComparer.Ordinal);
        if (requiredKinds.Any(kind => !kinds.Contains(kind)))
        {
            failures.Add("scenario_required_event_missing");
        }

        if (forbiddenKinds.Any(kinds.Contains))
        {
            failures.Add("scenario_forbidden_event_present");
        }

        if (expectation.TerminalKind is not null
            && !string.Equals(
                projection.TerminalKind,
                expectation.TerminalKind,
                StringComparison.Ordinal))
        {
            failures.Add("scenario_terminal_kind_mismatch");
        }

        if (expectation.MaximumTurns.HasValue
            && projection.Turns > expectation.MaximumTurns.Value)
        {
            failures.Add("scenario_turn_limit_exceeded");
        }

        if (expectation.MaximumActionRequests.HasValue
            && projection.ActionRequests
            > expectation.MaximumActionRequests.Value)
        {
            failures.Add("scenario_action_limit_exceeded");
        }

        if (expectation.MaximumToolCalls.HasValue
            && projection.ToolCalls > expectation.MaximumToolCalls.Value)
        {
            failures.Add("scenario_tool_call_limit_exceeded");
        }

        if (expectation.MaximumProviderAttempts.HasValue
            && projection.ProviderDispatches
            > expectation.MaximumProviderAttempts.Value)
        {
            failures.Add("scenario_provider_attempt_limit_exceeded");
        }

        var usage = analysis.Trajectory.Usage;
        if (expectation.MaximumInputTokens.HasValue
            && usage.InputTokens > expectation.MaximumInputTokens.Value)
        {
            failures.Add("scenario_input_token_limit_exceeded");
        }

        if (expectation.MaximumOutputTokens.HasValue
            && usage.OutputTokens > expectation.MaximumOutputTokens.Value)
        {
            failures.Add("scenario_output_token_limit_exceeded");
        }

        if (expectation.MaximumTotalTokens.HasValue
            && usage.TotalTokens > expectation.MaximumTotalTokens.Value)
        {
            failures.Add("scenario_total_token_limit_exceeded");
        }

        if (expectation.MaximumCostUsd is not null
            && (!RuntimeTraceNumbers.TryCompareCosts(
                    usage.CostUsd,
                    expectation.MaximumCostUsd,
                    out var costComparison)
                || costComparison > 0))
        {
            failures.Add("scenario_cost_limit_exceeded");
        }

        if (expectation.ExpectedTrajectoryDigest is not null
            && !string.Equals(
                expectation.ExpectedTrajectoryDigest,
                analysis.Trajectory.Digest,
                StringComparison.Ordinal))
        {
            failures.Add("scenario_trajectory_digest_mismatch");
        }

        if (expectation.RequireValidReplay)
        {
            failures.UnionWith(
                analysis.Trajectory.AssertionFailureCodes);
        }

        if (expectation.RequireSettledUsage
            && (usage.HasUnaccountedUsage
                || usage.UnaccountedProviderAttempts > 0
                || string.Equals(
                    usage.Availability,
                    UsageAvailabilityStates.CostUnavailable,
                    StringComparison.Ordinal)))
        {
            failures.Add("scenario_usage_unsettled");
        }

        if (expectation.RequireBudgetCompliance
            && !analysis.Trajectory.BudgetCompliant)
        {
            failures.Add("scenario_budget_noncompliant");
        }

        var ordered = failures
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        return new RuntimeScenarioEvaluation(
            ordered.Length == 0,
            new ReadOnlyCollection<string>(ordered),
            analysis);
    }

    private static string SnapshotEventKind(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Encoding.UTF8.GetByteCount(value) > 512)
        {
            throw new RuntimeContentLimitException(
                "expectation",
                "scenario_event_kind_invalid",
                "A scenario event kind is empty or exceeds its byte limit.");
        }

        return value;
    }

    private static void EnsureOptionalBounded(
        string? value,
        int maximumUtf8Bytes,
        string limitCode)
    {
        if (value is not null
            && (string.IsNullOrWhiteSpace(value)
                || Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes))
        {
            throw new RuntimeContentLimitException(
                "expectation",
                limitCode,
                "A scenario expectation string is empty or exceeds its "
                + "byte limit.");
        }
    }
}
