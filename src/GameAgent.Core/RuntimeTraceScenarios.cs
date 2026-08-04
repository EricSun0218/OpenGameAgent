using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class RuntimeScenarioJsonLinesOptions
{
    public int MaxScenarios { get; set; } = 1_000;

    public int MaxInputUtf8Bytes { get; set; } = 32 * 1_048_576;

    public int MaxLineUtf8Bytes { get; set; } = 8 * 1_048_576;

    public int MaxOutputUtf8Bytes { get; set; } = 16 * 1_048_576;

    public int MaxEventsPerScenario { get; set; } = 10_000;

    public int MaxAggregateEvents { get; set; } = 50_000;

    public int MaxAggregateTraceUtf8Bytes { get; set; } = 32 * 1_048_576;

    public int MaxJsonDepth { get; set; } = 40;

    public int MaxJsonNodesPerLine { get; set; } = 1_048_576;

    internal RuntimeScenarioJsonLinesOptions Snapshot()
    {
        if (MaxScenarios is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxScenarios));
        }

        if (MaxInputUtf8Bytes is < 1_024 or > 512 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxInputUtf8Bytes));
        }

        if (MaxLineUtf8Bytes is < 1_024 or > 128 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxLineUtf8Bytes));
        }

        if (MaxOutputUtf8Bytes is < 1_024 or > 512 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxOutputUtf8Bytes));
        }

        if (MaxEventsPerScenario is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxEventsPerScenario));
        }

        if (MaxAggregateEvents is < 1 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxAggregateEvents));
        }

        if (MaxAggregateTraceUtf8Bytes is < 1_024
            or > 512 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxAggregateTraceUtf8Bytes));
        }

        if (MaxJsonDepth is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxJsonDepth));
        }

        if (MaxJsonNodesPerLine is < 1 or > 4_194_304)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxJsonNodesPerLine));
        }

        return new RuntimeScenarioJsonLinesOptions
        {
            MaxScenarios = MaxScenarios,
            MaxInputUtf8Bytes = MaxInputUtf8Bytes,
            MaxLineUtf8Bytes = Math.Min(
                MaxLineUtf8Bytes,
                MaxInputUtf8Bytes),
            MaxOutputUtf8Bytes = MaxOutputUtf8Bytes,
            MaxEventsPerScenario = MaxEventsPerScenario,
            MaxAggregateEvents = MaxAggregateEvents,
            MaxAggregateTraceUtf8Bytes =
                MaxAggregateTraceUtf8Bytes,
            MaxJsonDepth = MaxJsonDepth,
            MaxJsonNodesPerLine = MaxJsonNodesPerLine
        };
    }
}

public sealed class RuntimeScenarioFormatException : FormatException
{
    public RuntimeScenarioFormatException(string formatCode, string message)
        : base(message)
    {
        FormatCode = formatCode;
    }

    public string FormatCode { get; }
}

public sealed class RuntimeScenarioDefinition
{
    public RuntimeScenarioDefinition(
        string scenarioId,
        IEnumerable<RuntimeEvent> events,
        RuntimeScenarioExpectation? expectation = null)
    {
        if (string.IsNullOrWhiteSpace(scenarioId))
        {
            throw new ArgumentException(
                "A scenario ID is required.",
                nameof(scenarioId));
        }

        ScenarioId = scenarioId;
        Events = events
            ?? throw new ArgumentNullException(nameof(events));
        Expectation = expectation ?? new RuntimeScenarioExpectation();
    }

    public string ScenarioId { get; }

    public IEnumerable<RuntimeEvent> Events { get; }

    public RuntimeScenarioExpectation Expectation { get; }
}

public sealed class RuntimeScenarioBatchItemResult
{
    internal RuntimeScenarioBatchItemResult(
        string scenarioId,
        bool passed,
        IReadOnlyList<string> failureCodes,
        RuntimeTraceAnalysis analysis,
        RuntimeReplayResult replay)
    {
        ScenarioId = scenarioId;
        Passed = passed;
        FailureCodes = failureCodes;
        Analysis = analysis;
        Replay = replay;
    }

    public string ScenarioId { get; }

    public bool Passed { get; }

    public IReadOnlyList<string> FailureCodes { get; }

    public RuntimeTraceAnalysis Analysis { get; }

    public RuntimeReplayResult Replay { get; }
}

public sealed class RuntimeScenarioAggregate
{
    internal RuntimeScenarioAggregate(
        int scenarioCount,
        int passedScenarios,
        int failedScenarios,
        int replayPassedScenarios,
        int replayFailedScenarios,
        long eventCount,
        long turns,
        long toolCalls,
        long actionRequests,
        long providerAttempts,
        long inputTokens,
        long outputTokens,
        string costUsd,
        string costAvailability)
    {
        ScenarioCount = scenarioCount;
        PassedScenarios = passedScenarios;
        FailedScenarios = failedScenarios;
        ReplayPassedScenarios = replayPassedScenarios;
        ReplayFailedScenarios = replayFailedScenarios;
        EventCount = eventCount;
        Turns = turns;
        ToolCalls = toolCalls;
        ActionRequests = actionRequests;
        ProviderAttempts = providerAttempts;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CostUsd = costUsd;
        CostAvailability = costAvailability;
    }

    public int ScenarioCount { get; }

    public int PassedScenarios { get; }

    public int FailedScenarios { get; }

    public int ReplayPassedScenarios { get; }

    public int ReplayFailedScenarios { get; }

    public long EventCount { get; }

    public long Turns { get; }

    public long ToolCalls { get; }

    public long ActionRequests { get; }

    public long ProviderAttempts { get; }

    public long InputTokens { get; }

    public long OutputTokens { get; }

    public long TotalTokens => checked(InputTokens + OutputTokens);

    public string CostUsd { get; }

    public string CostAvailability { get; }
}

public sealed class RuntimeScenarioBatchResult
{
    internal RuntimeScenarioBatchResult(
        IReadOnlyList<RuntimeScenarioBatchItemResult> results,
        RuntimeScenarioAggregate aggregate,
        string jsonLines,
        string digest)
    {
        Results = results;
        Aggregate = aggregate;
        JsonLines = jsonLines;
        Digest = digest;
    }

    public IReadOnlyList<RuntimeScenarioBatchItemResult> Results { get; }

    public RuntimeScenarioAggregate Aggregate { get; }

    public string JsonLines { get; }

    public string Digest { get; }
}

public sealed class RuntimeScenarioBatchRunner
{
    private const string ScenarioSchema = "game-agent.scenario.v1";
    private const string ResultSchema = "game-agent.scenario-result.v1";
    private const string AggregateSchema =
        "game-agent.scenario-aggregate.v1";

    private readonly RuntimeScenarioJsonLinesOptions _options;
    private readonly RuntimeTraceAnalysisOptions _analysisOptions;

    public RuntimeScenarioBatchRunner(
        RuntimeScenarioJsonLinesOptions? options = null,
        RuntimeTraceAnalysisOptions? analysisOptions = null)
    {
        _options =
            (options ?? new RuntimeScenarioJsonLinesOptions()).Snapshot();
        var requested =
            (analysisOptions ?? new RuntimeTraceAnalysisOptions()).Snapshot();
        _analysisOptions = new RuntimeTraceAnalysisOptions
        {
            MaxEvents = Math.Min(
                requested.MaxEvents,
                _options.MaxEventsPerScenario),
            MaxUtf8Bytes = requested.MaxUtf8Bytes,
            MaxEventUtf8Bytes = requested.MaxEventUtf8Bytes,
            MaxJsonDepth = requested.MaxJsonDepth,
            MaxJsonNodesPerEvent = requested.MaxJsonNodesPerEvent
        }.Snapshot();
    }

    public RuntimeScenarioBatchResult Run(
        IEnumerable<RuntimeScenarioDefinition> scenarios)
    {
        if (scenarios is null)
        {
            throw new ArgumentNullException(nameof(scenarios));
        }

        var results = new List<RuntimeScenarioBatchItemResult>();
        var scenarioIds = new HashSet<string>(StringComparer.Ordinal);
        long aggregateEvents = 0;
        long aggregateTraceBytes = 0;
        using var enumerator = scenarios.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (results.Count >= _options.MaxScenarios)
            {
                throw Limit(
                    "scenario_batch_count_exceeded",
                    "The scenario batch contains too many scenarios.");
            }

            var scenario = enumerator.Current
                ?? throw new ArgumentException(
                    "Scenario batches cannot contain null entries.",
                    nameof(scenarios));
            if (!scenarioIds.Add(scenario.ScenarioId))
            {
                throw new RuntimeScenarioFormatException(
                    "scenario_duplicate_id",
                    "Scenario IDs must be unique within a batch.");
            }

            if (Encoding.UTF8.GetByteCount(scenario.ScenarioId) > 1_024)
            {
                throw Limit(
                    "scenario_id_bytes_exceeded",
                    "A scenario ID exceeds its byte limit.");
            }

            var analysis = new RuntimeTraceAnalyzer(
                _analysisOptions).Analyze(scenario.Events);
            aggregateEvents = checked(
                aggregateEvents + analysis.Projection.EventCount);
            if (aggregateEvents > _options.MaxAggregateEvents)
            {
                throw BatchLimit(
                    "scenario_batch_event_count_exceeded",
                    "The scenario batch contains too many trace events.");
            }

            aggregateTraceBytes = checked(
                aggregateTraceBytes
                + analysis.MaterializedUtf8Bytes);
            if (aggregateTraceBytes
                > _options.MaxAggregateTraceUtf8Bytes)
            {
                throw BatchLimit(
                    "scenario_batch_trace_bytes_exceeded",
                    "The scenario batch trace data exceeds its byte limit.");
            }

            var evaluation = new RuntimeScenarioEvaluator(
                _analysisOptions).Evaluate(
                    analysis,
                    scenario.Expectation);
            var replay = new RecordedRuntimeReplayHarness(
                _analysisOptions).Replay(analysis);
            var failures = new ReadOnlyCollection<string>(
                evaluation.FailureCodes
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray());
            results.Add(
                new RuntimeScenarioBatchItemResult(
                    scenario.ScenarioId,
                    failures.Count == 0,
                    failures,
                    analysis,
                    replay));
        }

        var aggregate = Aggregate(results);
        var jsonLines = WriteJsonLines(results, aggregate);
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "runtime-scenario-batch-v1");
        digest.Add("jsonLines", jsonLines);
        return new RuntimeScenarioBatchResult(
            new ReadOnlyCollection<RuntimeScenarioBatchItemResult>(
                results),
            aggregate,
            jsonLines,
            digest.Finish());
    }

    public RuntimeScenarioBatchResult RunJsonLines(string jsonLines)
    {
        if (jsonLines is null)
        {
            throw new ArgumentNullException(nameof(jsonLines));
        }

        if (Encoding.UTF8.GetByteCount(jsonLines)
            > _options.MaxInputUtf8Bytes)
        {
            throw Limit(
                "scenario_jsonl_input_bytes_exceeded",
                "Scenario JSONL exceeds its aggregate input limit.");
        }

        var scenarios = new List<RuntimeScenarioDefinition>();
        var lineStart = 0;
        for (var index = 0; index <= jsonLines.Length; index++)
        {
            if (index < jsonLines.Length && jsonLines[index] != '\n')
            {
                continue;
            }

            var length = index - lineStart;
            if (length > 0 && jsonLines[lineStart + length - 1] == '\r')
            {
                length--;
            }

            var line = jsonLines.Substring(lineStart, length);
            lineStart = index + 1;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (scenarios.Count >= _options.MaxScenarios)
            {
                throw Limit(
                    "scenario_jsonl_count_exceeded",
                    "Scenario JSONL contains too many scenarios.");
            }

            if (Encoding.UTF8.GetByteCount(line)
                > _options.MaxLineUtf8Bytes)
            {
                throw Limit(
                    "scenario_jsonl_line_bytes_exceeded",
                    "A scenario JSONL line exceeds its byte limit.");
            }

            scenarios.Add(ParseLine(line));
        }

        return Run(scenarios);
    }

    private RuntimeScenarioDefinition ParseLine(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                line,
                new JsonDocumentOptions
                {
                    MaxDepth = _options.MaxJsonDepth
                });
        }
        catch (JsonException exception)
        {
            throw new RuntimeScenarioFormatException(
                "scenario_jsonl_invalid_json",
                "A scenario line is not valid JSON: "
                + exception.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            try
            {
                JsonValueInspector.ValidateAndMeasure(
                    root,
                    new JsonValueLimits(
                        _options.MaxLineUtf8Bytes,
                        _options.MaxJsonDepth,
                        _options.MaxJsonNodesPerLine,
                        _options.MaxLineUtf8Bytes,
                        _options.MaxJsonNodesPerLine),
                    "jsonLines");
            }
            catch (RuntimeContentLimitException exception)
            {
                throw new RuntimeScenarioFormatException(
                    "scenario_jsonl_invalid_value",
                    "A scenario line contains an invalid JSON value: "
                    + exception.Message);
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Format(
                    "scenario_jsonl_root_invalid",
                    "A scenario line must contain an object.");
            }

            RejectUnknown(root, "schema", "scenarioId", "events", "expectation");
            var schema = RequiredString(root, "schema");
            if (!string.Equals(schema, ScenarioSchema, StringComparison.Ordinal))
            {
                throw Format(
                    "scenario_jsonl_schema_unsupported",
                    "The scenario schema is not supported.");
            }

            var scenarioId = RequiredString(root, "scenarioId");
            if (!root.TryGetProperty("events", out var eventArray)
                || eventArray.ValueKind != JsonValueKind.Array)
            {
                throw Format(
                    "scenario_jsonl_events_invalid",
                    "A scenario line must contain an events array.");
            }

            var events = new List<RuntimeEvent>();
            foreach (var item in eventArray.EnumerateArray())
            {
                if (events.Count >= _options.MaxEventsPerScenario)
                {
                    throw Limit(
                        "scenario_jsonl_event_count_exceeded",
                        "A scenario contains too many events.");
                }

                try
                {
                    events.Add(
                        ProtocolJson.DeserializeRuntimeEvent(
                            item.GetRawText()));
                }
                catch (Exception exception)
                    when (exception is JsonException
                          or InvalidOperationException
                          or NotSupportedException)
                {
                    throw Format(
                        "scenario_jsonl_event_invalid",
                        "A scenario contains an invalid runtime event.");
                }
            }

            var expectation = root.TryGetProperty(
                    "expectation",
                    out var expectationElement)
                ? ParseExpectation(expectationElement)
                : new RuntimeScenarioExpectation();
            return new RuntimeScenarioDefinition(
                scenarioId,
                new ReadOnlyCollection<RuntimeEvent>(events),
                expectation);
        }
    }

    private static RuntimeScenarioExpectation ParseExpectation(
        JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Format(
                "scenario_jsonl_expectation_invalid",
                "A scenario expectation must be an object.");
        }

        RejectUnknown(
            value,
            "requiredEventKinds",
            "forbiddenEventKinds",
            "terminalKind",
            "maximumTurns",
            "maximumActionRequests",
            "maximumToolCalls",
            "maximumProviderAttempts",
            "maximumInputTokens",
            "maximumOutputTokens",
            "maximumTotalTokens",
            "maximumCostUsd",
            "expectedTrajectoryDigest",
            "requireValidReplay",
            "requireSettledUsage",
            "requireBudgetCompliance");
        return new RuntimeScenarioExpectation
        {
            RequiredEventKinds = OptionalStringArray(
                value,
                "requiredEventKinds"),
            ForbiddenEventKinds = OptionalStringArray(
                value,
                "forbiddenEventKinds"),
            TerminalKind = OptionalString(value, "terminalKind"),
            MaximumTurns = OptionalNonNegativeInt(value, "maximumTurns"),
            MaximumActionRequests = OptionalNonNegativeInt(
                value,
                "maximumActionRequests"),
            MaximumToolCalls = OptionalNonNegativeInt(
                value,
                "maximumToolCalls"),
            MaximumProviderAttempts = OptionalNonNegativeInt(
                value,
                "maximumProviderAttempts"),
            MaximumInputTokens = OptionalNonNegativeInt(
                value,
                "maximumInputTokens"),
            MaximumOutputTokens = OptionalNonNegativeInt(
                value,
                "maximumOutputTokens"),
            MaximumTotalTokens = OptionalNonNegativeInt(
                value,
                "maximumTotalTokens"),
            MaximumCostUsd = OptionalString(value, "maximumCostUsd"),
            ExpectedTrajectoryDigest = OptionalString(
                value,
                "expectedTrajectoryDigest"),
            RequireValidReplay = OptionalBoolean(
                value,
                "requireValidReplay"),
            RequireSettledUsage = OptionalBoolean(
                value,
                "requireSettledUsage"),
            RequireBudgetCompliance = OptionalBoolean(
                value,
                "requireBudgetCompliance")
        };
    }

    private RuntimeScenarioAggregate Aggregate(
        IReadOnlyList<RuntimeScenarioBatchItemResult> results)
    {
        var passed = 0;
        var replayPassed = 0;
        long events = 0;
        long turns = 0;
        long tools = 0;
        long actions = 0;
        long providers = 0;
        long inputTokens = 0;
        long outputTokens = 0;
        var cost = "0";
        var costUnavailable = false;
        var costInvalid = false;
        foreach (var result in results)
        {
            if (result.Passed)
            {
                passed++;
            }

            if (result.Replay.Passed)
            {
                replayPassed++;
            }

            events = checked(events + result.Analysis.Projection.EventCount);
            turns = checked(turns + result.Analysis.Projection.Turns);
            tools = checked(tools + result.Analysis.Projection.ToolCalls);
            actions = checked(
                actions + result.Analysis.Projection.ActionRequests);
            providers = checked(
                providers
                + result.Analysis.Projection.ProviderDispatches);
            inputTokens = checked(
                inputTokens
                + result.Analysis.Trajectory.Usage.InputTokens);
            outputTokens = checked(
                outputTokens
                + result.Analysis.Trajectory.Usage.OutputTokens);
            var usage = result.Analysis.Trajectory.Usage;
            if (!string.Equals(
                    usage.Availability,
                    UsageAvailabilityStates.CostAvailable,
                    StringComparison.Ordinal)
                || usage.HasUnaccountedUsage)
            {
                costUnavailable = true;
            }

            if (!RuntimeTraceNumbers.TryAddCosts(
                    cost,
                    usage.CostUsd,
                    out var aggregateCost))
            {
                costInvalid = true;
            }
            else
            {
                cost = aggregateCost;
            }
        }

        return new RuntimeScenarioAggregate(
            results.Count,
            passed,
            results.Count - passed,
            replayPassed,
            results.Count - replayPassed,
            events,
            turns,
            tools,
            actions,
            providers,
            inputTokens,
            outputTokens,
            costInvalid
                ? "invalid"
                : costUnavailable
                    ? "unavailable"
                    : cost,
            costInvalid
                ? "invalid"
                : costUnavailable
                    ? "unavailable"
                    : "available");
    }

    private string WriteJsonLines(
        IReadOnlyList<RuntimeScenarioBatchItemResult> results,
        RuntimeScenarioAggregate aggregate)
    {
        var output = new StringBuilder();
        var bytes = 0;
        foreach (var result in results)
        {
            AddOutputLine(
                WriteResultLine(result),
                output,
                ref bytes);
        }

        AddOutputLine(
            WriteAggregateLine(aggregate),
            output,
            ref bytes);
        return output.ToString();
    }

    private void AddOutputLine(
        string line,
        StringBuilder output,
        ref int bytes)
    {
        var lineBytes = checked(Encoding.UTF8.GetByteCount(line) + 1);
        if (checked(bytes + lineBytes) > _options.MaxOutputUtf8Bytes)
        {
            throw Limit(
                "scenario_jsonl_output_bytes_exceeded",
                "Scenario result JSONL exceeds its output limit.");
        }

        output.Append(line);
        output.Append('\n');
        bytes += lineBytes;
    }

    private static string WriteResultLine(
        RuntimeScenarioBatchItemResult result)
    {
        return WriteObject(
            writer =>
            {
                writer.WriteString("schema", ResultSchema);
                writer.WriteString("scenarioId", result.ScenarioId);
                writer.WriteBoolean("passed", result.Passed);
                writer.WritePropertyName("failureCodes");
                writer.WriteStartArray();
                foreach (var code in result.FailureCodes)
                {
                    writer.WriteStringValue(code);
                }

                writer.WriteEndArray();
                writer.WriteString(
                    "trajectoryDigest",
                    result.Analysis.Trajectory.Digest);
                writer.WriteString(
                    "replayDigest",
                    result.Replay.ReplayDigest);
                writer.WriteBoolean(
                    "replayPassed",
                    result.Replay.Passed);
                writer.WritePropertyName("replayFailureCodes");
                writer.WriteStartArray();
                foreach (var code in result.Replay.FailureCodes)
                {
                    writer.WriteStringValue(code);
                }

                writer.WriteEndArray();
                writer.WriteNumber(
                    "eventCount",
                    result.Analysis.Projection.EventCount);
            });
    }

    private static string WriteAggregateLine(
        RuntimeScenarioAggregate aggregate)
    {
        return WriteObject(
            writer =>
            {
                writer.WriteString("schema", AggregateSchema);
                writer.WriteNumber(
                    "scenarioCount",
                    aggregate.ScenarioCount);
                writer.WriteNumber(
                    "passedScenarios",
                    aggregate.PassedScenarios);
                writer.WriteNumber(
                    "failedScenarios",
                    aggregate.FailedScenarios);
                writer.WriteNumber(
                    "replayPassedScenarios",
                    aggregate.ReplayPassedScenarios);
                writer.WriteNumber(
                    "replayFailedScenarios",
                    aggregate.ReplayFailedScenarios);
                writer.WriteNumber("eventCount", aggregate.EventCount);
                writer.WriteNumber("turns", aggregate.Turns);
                writer.WriteNumber("toolCalls", aggregate.ToolCalls);
                writer.WriteNumber(
                    "actionRequests",
                    aggregate.ActionRequests);
                writer.WriteNumber(
                    "providerAttempts",
                    aggregate.ProviderAttempts);
                writer.WriteNumber(
                    "inputTokens",
                    aggregate.InputTokens);
                writer.WriteNumber(
                    "outputTokens",
                    aggregate.OutputTokens);
                writer.WriteNumber(
                    "totalTokens",
                    aggregate.TotalTokens);
                writer.WriteString("costUsd", aggregate.CostUsd);
                writer.WriteString(
                    "costAvailability",
                    aggregate.CostAvailability);
            });
    }

    private static string WriteObject(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void RejectUnknown(
        JsonElement value,
        params string[] allowed)
    {
        var names = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Contains(property.Name))
            {
                throw Format(
                    "scenario_jsonl_unknown_property",
                    "A scenario contains an unknown property.");
            }
        }
    }

    private static string RequiredString(
        JsonElement value,
        string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw Format(
                "scenario_jsonl_required_property_invalid",
                "A required scenario string is missing or invalid.");
        }

        return property.GetString()!;
    }

    private static string? OptionalString(
        JsonElement value,
        string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw Format(
                "scenario_jsonl_expectation_invalid",
                "An optional scenario string is invalid.");
        }

        return property.GetString();
    }

    private static IReadOnlyList<string> OptionalStringArray(
        JsonElement value,
        string name)
    {
        if (!value.TryGetProperty(name, out var property))
        {
            return Array.Empty<string>();
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            throw Format(
                "scenario_jsonl_expectation_invalid",
                "A scenario event-kind set must be an array.");
        }

        var items = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (items.Count >= 256
                || item.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw Format(
                    "scenario_jsonl_expectation_invalid",
                    "A scenario event-kind set is invalid.");
            }

            items.Add(item.GetString()!);
        }

        return new ReadOnlyCollection<string>(items);
    }

    private static int? OptionalNonNegativeInt(
        JsonElement value,
        string name)
    {
        if (!value.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var result)
            || result < 0)
        {
            throw Format(
                "scenario_jsonl_expectation_invalid",
                "A scenario limit must be a non-negative integer.");
        }

        return result;
    }

    private static bool OptionalBoolean(
        JsonElement value,
        string name)
    {
        if (!value.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind is not JsonValueKind.True
            and not JsonValueKind.False)
        {
            throw Format(
                "scenario_jsonl_expectation_invalid",
                "A scenario flag must be a boolean.");
        }

        return property.GetBoolean();
    }

    private static RuntimeScenarioFormatException Format(
        string code,
        string message)
    {
        return new RuntimeScenarioFormatException(code, message);
    }

    private static RuntimeContentLimitException Limit(
        string code,
        string message)
    {
        return new RuntimeContentLimitException("jsonLines", code, message);
    }

    private static RuntimeContentLimitException BatchLimit(
        string code,
        string message)
    {
        return new RuntimeContentLimitException("scenarios", code, message);
    }
}
