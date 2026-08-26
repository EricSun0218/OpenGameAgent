using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Extensions;

namespace OpenGameAgent.DevTools;

public sealed class GameAgentLatencyBreakdown
{
    internal GameAgentLatencyBreakdown(
        double queueMilliseconds,
        double inputPreparationMilliseconds,
        double sessionLoadMilliseconds,
        double contextBuildMilliseconds,
        double toolCollectionMilliseconds,
        double skillSelectionMilliseconds,
        double? timeToFirstResponseMilliseconds,
        double? providerTimeToFirstResponseMilliseconds,
        double? responseCompleteMilliseconds,
        double? timeToFirstToolMilliseconds,
        double modelRequestMilliseconds,
        double toolExecutionMilliseconds,
        double hostActionMilliseconds,
        double durableActionFrameworkMilliseconds,
        double approvalWaitMilliseconds,
        double frameworkOverheadMilliseconds,
        double executionMilliseconds,
        double totalMilliseconds)
    {
        QueueMilliseconds = queueMilliseconds;
        InputPreparationMilliseconds = inputPreparationMilliseconds;
        SessionLoadMilliseconds = sessionLoadMilliseconds;
        ContextBuildMilliseconds = contextBuildMilliseconds;
        ToolCollectionMilliseconds = toolCollectionMilliseconds;
        SkillSelectionMilliseconds = skillSelectionMilliseconds;
        TimeToFirstResponseMilliseconds = timeToFirstResponseMilliseconds;
        ProviderTimeToFirstResponseMilliseconds = providerTimeToFirstResponseMilliseconds;
        ResponseCompleteMilliseconds = responseCompleteMilliseconds;
        TimeToFirstToolMilliseconds = timeToFirstToolMilliseconds;
        ModelRequestMilliseconds = modelRequestMilliseconds;
        ToolExecutionMilliseconds = toolExecutionMilliseconds;
        HostActionMilliseconds = hostActionMilliseconds;
        DurableActionFrameworkMilliseconds = durableActionFrameworkMilliseconds;
        ApprovalWaitMilliseconds = approvalWaitMilliseconds;
        FrameworkOverheadMilliseconds = frameworkOverheadMilliseconds;
        ExecutionMilliseconds = executionMilliseconds;
        TotalMilliseconds = totalMilliseconds;
    }

    public double QueueMilliseconds { get; }
    public double InputPreparationMilliseconds { get; }
    public double SessionLoadMilliseconds { get; }
    public double ContextBuildMilliseconds { get; }
    public double ToolCollectionMilliseconds { get; }
    public double SkillSelectionMilliseconds { get; }
    public double? TimeToFirstResponseMilliseconds { get; }
    public double? ProviderTimeToFirstResponseMilliseconds { get; }
    public double? ResponseCompleteMilliseconds { get; }
    public double? TimeToFirstToolMilliseconds { get; }
    public double ModelRequestMilliseconds { get; }
    public double ToolExecutionMilliseconds { get; }
    public double HostActionMilliseconds { get; }
    public double DurableActionFrameworkMilliseconds { get; }
    public double ApprovalWaitMilliseconds { get; }
    public double FrameworkOverheadMilliseconds { get; }
    public double ExecutionMilliseconds { get; }
    public double TotalMilliseconds { get; }
}

public sealed class GameAgentToolMetric
{
    internal GameAgentToolMetric(
        string tool,
        double durationMilliseconds,
        bool succeeded,
        string failureCategory,
        bool outcomeUncertain,
        string? operation,
        string? provider,
        string? model,
        string? operationId,
        string? actionStatus,
        double hostActionMilliseconds,
        double durableActionFrameworkMilliseconds,
        double approvalWaitMilliseconds,
        bool duplicateExecutionPrevented,
        bool recovered)
    {
        Tool = tool;
        DurationMilliseconds = durationMilliseconds;
        Succeeded = succeeded;
        FailureCategory = failureCategory;
        OutcomeUncertain = outcomeUncertain;
        Operation = operation;
        Provider = provider;
        Model = model;
        OperationId = operationId;
        ActionStatus = actionStatus;
        HostActionMilliseconds = hostActionMilliseconds;
        DurableActionFrameworkMilliseconds = durableActionFrameworkMilliseconds;
        ApprovalWaitMilliseconds = approvalWaitMilliseconds;
        DuplicateExecutionPrevented = duplicateExecutionPrevented;
        Recovered = recovered;
    }

    public string Tool { get; }
    public double DurationMilliseconds { get; }
    public bool Succeeded { get; }
    public string FailureCategory { get; }
    public bool OutcomeUncertain { get; }
    public string? Operation { get; }
    public string? Provider { get; }
    public string? Model { get; }
    public string? OperationId { get; }
    public string? ActionStatus { get; }
    public double HostActionMilliseconds { get; }
    public double DurableActionFrameworkMilliseconds { get; }
    public double ApprovalWaitMilliseconds { get; }
    public bool DuplicateExecutionPrevented { get; }
    public bool Recovered { get; }
}

public sealed class GameAgentContextProviderMetric
{
    internal GameAgentContextProviderMetric(
        string provider,
        string phase,
        string? extensionId,
        int sliceCount,
        double durationMilliseconds)
    {
        Provider = provider;
        Phase = phase;
        ExtensionId = extensionId;
        SliceCount = sliceCount;
        DurationMilliseconds = durationMilliseconds;
    }

    public string Provider { get; }

    public string Phase { get; }

    public string? ExtensionId { get; }

    public int SliceCount { get; }

    public double DurationMilliseconds { get; }
}

public sealed class GameAgentMemorySearchMetric
{
    internal GameAgentMemorySearchMetric(
        string source,
        string stage,
        double durationMilliseconds,
        int scannedCount,
        int candidateCount,
        bool reused)
    {
        Source = source;
        Stage = stage;
        DurationMilliseconds = durationMilliseconds;
        ScannedCount = scannedCount;
        CandidateCount = candidateCount;
        Reused = reused;
    }

    public string Source { get; }

    public string Stage { get; }

    public double DurationMilliseconds { get; }

    public int ScannedCount { get; }

    public int CandidateCount { get; }

    public bool Reused { get; }
}

public sealed class GameAgentRunPerformance
{
    internal GameAgentRunPerformance(
        string sessionId,
        string actorId,
        string inputId,
        string? provider,
        string? model,
        string status,
        GameAgentLatencyBreakdown latency,
        IReadOnlyList<GameAgentToolMetric> tools,
        IReadOnlyList<GameAgentContextProviderMetric> contextProviders,
        IReadOnlyList<GameAgentMemorySearchMetric> memorySearchStages,
        int exactToolRepeatAdvisories,
        int exactToolRepeatTerminations,
        int retries,
        int fallbacks,
        long totalTokens,
        bool costKnown,
        double? totalCost)
    {
        SessionId = sessionId;
        ActorId = actorId;
        InputId = inputId;
        Provider = provider;
        Model = model;
        Status = status;
        Latency = latency;
        Tools = tools;
        ContextProviders = contextProviders;
        MemorySearchStages = memorySearchStages;
        ExactToolRepeatAdvisories = exactToolRepeatAdvisories;
        ExactToolRepeatTerminations = exactToolRepeatTerminations;
        Retries = retries;
        Fallbacks = fallbacks;
        TotalTokens = totalTokens;
        CostKnown = costKnown;
        TotalCost = totalCost;
    }

    public string SessionId { get; }
    public string ActorId { get; }
    public string InputId { get; }
    public string? Provider { get; }
    public string? Model { get; }
    public string Status { get; }
    public GameAgentLatencyBreakdown Latency { get; }
    public IReadOnlyList<GameAgentToolMetric> Tools { get; }
    public IReadOnlyList<GameAgentContextProviderMetric> ContextProviders { get; }
    public IReadOnlyList<GameAgentMemorySearchMetric> MemorySearchStages { get; }
    public int ExactToolRepeatAdvisories { get; }
    public int ExactToolRepeatTerminations { get; }
    public int Retries { get; }
    public int Fallbacks { get; }
    public long TotalTokens { get; }
    public bool CostKnown { get; }
    public double? TotalCost { get; }
    public int ToolCalls => Tools.Count;
    public int SuccessfulTools => Tools.Count(value => value.Succeeded);
    public int RetryableToolFailures => Tools.Count(value => value.FailureCategory == "Transient");
    public int RuleToolFailures => Tools.Count(value => value.FailureCategory is "RuleRejected" or "Authorization");
    public int TimedOutTools => Tools.Count(value => value.FailureCategory == "Timeout");
    public int CancelledTools => Tools.Count(value => value.FailureCategory == "Cancelled");
    public int WorldWrites => Tools.Count(value => value.OperationId is not null);
    public int UncertainWrites => Tools.Count(value => value.OperationId is not null && value.OutcomeUncertain);
    public int DuplicateWritesPrevented => Tools.Count(value => value.DuplicateExecutionPrevented);
    public int Replans => Tools.Count(value => value.Tool == "manage_task_plan" && value.Operation == "replace_remaining");
}

public sealed class GameAgentToolAggregate
{
    internal GameAgentToolAggregate(
        string tool,
        string failureCategory,
        string provider,
        string model,
        int calls,
        int successes,
        int worldWrites,
        int uncertainWrites,
        int duplicateWritesPrevented,
        int recoveries,
        double totalDurationMilliseconds)
    {
        Tool = tool;
        FailureCategory = failureCategory;
        Provider = provider;
        Model = model;
        Calls = calls;
        Successes = successes;
        WorldWrites = worldWrites;
        UncertainWrites = uncertainWrites;
        DuplicateWritesPrevented = duplicateWritesPrevented;
        Recoveries = recoveries;
        TotalDurationMilliseconds = totalDurationMilliseconds;
    }

    public string Tool { get; }
    public string FailureCategory { get; }
    public string Provider { get; }
    public string Model { get; }
    public int Calls { get; }
    public int Successes { get; }
    public int Failures => Calls - Successes;
    public double SuccessRate => Calls == 0 ? 1 : (double)Successes / Calls;
    public int WorldWrites { get; }
    public int UncertainWrites { get; }
    public int DuplicateWritesPrevented { get; }
    public int Recoveries { get; }
    public double TotalDurationMilliseconds { get; }
    public double AverageDurationMilliseconds => Calls == 0 ? 0 : TotalDurationMilliseconds / Calls;
}

/// <summary>
/// Derives stable, machine-readable latency, reliability, usage, and durable-write metrics from a
/// bounded trace recording. It never replays a provider, tool, or game action.
/// </summary>
public sealed class GameAgentPerformanceSummary
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private GameAgentPerformanceSummary(
        IReadOnlyList<GameAgentRunPerformance> runs,
        IReadOnlyList<GameAgentToolAggregate> toolAggregates)
    {
        Runs = runs;
        ToolAggregates = toolAggregates;
    }

    public IReadOnlyList<GameAgentRunPerformance> Runs { get; }
    public IReadOnlyList<GameAgentToolAggregate> ToolAggregates { get; }
    public int ToolCalls => Runs.Sum(value => value.ToolCalls);
    public int SuccessfulTools => Runs.Sum(value => value.SuccessfulTools);
    public double ToolSuccessRate => ToolCalls == 0 ? 1 : (double)SuccessfulTools / ToolCalls;
    public int RetryableToolFailures => Runs.Sum(value => value.RetryableToolFailures);
    public double RetryableToolFailureRate => ToolCalls == 0 ? 0 : (double)RetryableToolFailures / ToolCalls;
    public int RuleToolFailures => Runs.Sum(value => value.RuleToolFailures);
    public double RuleToolFailureRate => ToolCalls == 0 ? 0 : (double)RuleToolFailures / ToolCalls;
    public int TimedOutTools => Runs.Sum(value => value.TimedOutTools);
    public double ToolTimeoutRate => ToolCalls == 0 ? 0 : (double)TimedOutTools / ToolCalls;
    public int CancelledTools => Runs.Sum(value => value.CancelledTools);
    public double ToolCancellationRate => ToolCalls == 0 ? 0 : (double)CancelledTools / ToolCalls;
    public int UncertainWrites => Runs.Sum(value => value.UncertainWrites);
    public int WorldWrites => Runs.Sum(value => value.WorldWrites);
    public double UncertainWriteRate => WorldWrites == 0 ? 0 : (double)UncertainWrites / WorldWrites;
    public int DuplicateWritesPrevented => Runs.Sum(value => value.DuplicateWritesPrevented);
    public int Replans => Runs.Sum(value => value.Replans);
    public int ProviderRetries => Runs.Sum(value => value.Retries);
    public int ProviderFallbacks => Runs.Sum(value => value.Fallbacks);
    public int ExactToolRepeatAdvisories => Runs.Sum(value => value.ExactToolRepeatAdvisories);
    public int ExactToolRepeatTerminations => Runs.Sum(value => value.ExactToolRepeatTerminations);
    public long TotalTokens => Runs.Sum(value => value.TotalTokens);
    public bool CostKnown => Runs.All(value => value.CostKnown);
    public double? TotalCost => CostKnown ? Runs.Sum(value => value.TotalCost ?? 0) : null;

    public static GameAgentPerformanceSummary Create(GameAgentTraceRecording recording)
    {
        if (recording is null)
        {
            throw new ArgumentNullException(nameof(recording));
        }

        var runs = recording.Entries
            .GroupBy(value => (value.SessionId, value.ActorId, value.InputId))
            .SelectMany(group => SplitAttempts(group)
                .Select(attempt => CreateRun(group.Key.SessionId, group.Key.ActorId, group.Key.InputId, attempt)))
            .OrderBy(value => value.SessionId, StringComparer.Ordinal)
            .ThenBy(value => value.ActorId, StringComparer.Ordinal)
            .ThenBy(value => value.InputId, StringComparer.Ordinal)
            .ToArray();
        var aggregates = runs
            .SelectMany(run => run.Tools.Select(tool => (run, tool)))
            .GroupBy(value => new
            {
                value.tool.Tool,
                value.tool.FailureCategory,
                Provider = value.tool.Provider ?? value.run.Provider ?? "unknown",
                Model = value.tool.Model ?? value.run.Model ?? "unknown",
            })
            .Select(group => new GameAgentToolAggregate(
                group.Key.Tool,
                group.Key.FailureCategory,
                group.Key.Provider,
                group.Key.Model,
                group.Count(),
                group.Count(value => value.tool.Succeeded),
                group.Count(value => value.tool.OperationId is not null),
                group.Count(value => value.tool.OperationId is not null && value.tool.OutcomeUncertain),
                group.Count(value => value.tool.DuplicateExecutionPrevented),
                group.Count(value => value.tool.Recovered),
                group.Sum(value => value.tool.DurationMilliseconds)))
            .OrderBy(value => value.Tool, StringComparer.Ordinal)
            .ThenBy(value => value.FailureCategory, StringComparer.Ordinal)
            .ThenBy(value => value.Provider, StringComparer.Ordinal)
            .ThenBy(value => value.Model, StringComparer.Ordinal)
            .ToArray();
        return new GameAgentPerformanceSummary(
            Array.AsReadOnly(runs),
            Array.AsReadOnly(aggregates));
    }

    private static IEnumerable<IReadOnlyList<GameAgentTraceEntry>> SplitAttempts(
        IEnumerable<GameAgentTraceEntry> source)
    {
        var current = new List<GameAgentTraceEntry>();
        foreach (var entry in source.OrderBy(value => value.Sequence))
        {
            if (entry.Kind == "input.received" && current.Count > 0)
            {
                yield return current.ToArray();
                current.Clear();
            }

            current.Add(entry);
        }

        if (current.Count > 0)
        {
            yield return current.ToArray();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public string ToJsonLines() => string.Join(
        Environment.NewLine,
        Runs.Select(value => JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        })));

    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine($"Runs: {Runs.Count}; tools: {ToolCalls}; tool success: {ToolSuccessRate:P2}");
        text.AppendLine($"Retries: {ProviderRetries}; fallbacks: {ProviderFallbacks}; uncertain writes: {UncertainWrites}; duplicates blocked: {DuplicateWritesPrevented}");
        text.AppendLine($"Exact tool-repeat advisories: {ExactToolRepeatAdvisories}; terminated loops: {ExactToolRepeatTerminations}");
        text.AppendLine($"Tokens: {TotalTokens}; cost: {(CostKnown ? (TotalCost ?? 0).ToString("0.######", CultureInfo.InvariantCulture) : "unknown")}");
        foreach (var run in Runs)
        {
            text.AppendLine(
                $"{run.SessionId}/{run.ActorId}/{run.InputId} status={run.Status} total={run.Latency.TotalMilliseconds:0.###}ms ttft={Format(run.Latency.TimeToFirstResponseMilliseconds)} tools={run.ToolCalls}");
            foreach (var provider in run.ContextProviders)
            {
                text.AppendLine(
                    $"  context provider={provider.Provider} phase={provider.Phase} slices={provider.SliceCount} duration={provider.DurationMilliseconds:0.###}ms");
            }

            foreach (var memory in run.MemorySearchStages)
            {
                text.AppendLine(
                    $"  memory source={memory.Source} stage={memory.Stage} scanned={memory.ScannedCount} candidates={memory.CandidateCount} reused={memory.Reused} duration={memory.DurationMilliseconds:0.###}ms");
            }
        }

        return text.ToString();
    }

    private static string Format(double? value) =>
        value is null ? "n/a" : value.Value.ToString("0.###", CultureInfo.InvariantCulture) + "ms";

    private static GameAgentRunPerformance CreateRun(
        string sessionId,
        string actorId,
        string inputId,
        IEnumerable<GameAgentTraceEntry> source)
    {
        var entries = source.OrderBy(value => value.OperationalTimestamp).ThenBy(value => value.Sequence).ToArray();
        var input = entries.FirstOrDefault(value => value.Kind == "input.received");
        var completed = entries.LastOrDefault(value => value.Kind == "run.completed");
        var messageEnded = entries.LastOrDefault(value => value.Kind == "kernel.messageended");
        var firstResponse = entries.FirstOrDefault(value => value.Kind == "kernel.messagestarted");
        var firstTool = entries.FirstOrDefault(value => value.Kind == "kernel.toolstarted");
        var executionStart = input?.OperationalTimestamp ?? entries[0].OperationalTimestamp;
        var executionEnd = completed?.OperationalTimestamp ?? entries[^1].OperationalTimestamp;
        var queue = ReadDouble(input, "queueMilliseconds");
        var tools = CreateTools(entries);
        var modelRequests = CreateModelRequestDurations(entries);
        var hostActionDuration = tools.Sum(value => value.HostActionMilliseconds);
        var durableActionFrameworkDuration = tools.Sum(value => value.DurableActionFrameworkMilliseconds);
        var approvalWaitDuration = entries
            .Where(value => value.Kind == "tool.approval.completed")
            .Sum(value => ReadDouble(value, "waitMilliseconds"));
        var executionDuration = Milliseconds(executionStart, executionEnd);
        var toolExecutionDuration = tools.Sum(value => value.DurationMilliseconds);
        var modelRequestDuration = modelRequests.Sum(value => value.DurationMilliseconds);
        var providerTimeToFirstResponse = modelRequests
            .Where(value => value.TimeToFirstResponseMilliseconds is not null)
            .Select(value => value.TimeToFirstResponseMilliseconds)
            .FirstOrDefault();
        var usage = ReadObject(completed, "usage");
        var cost = usage is null ? null : ReadObject(usage.Value, "cost");
        var repeatEvents = entries.Where(value => value.Kind == "kernel.toolrepeatdetected").ToArray();
        var contextProviders = CreateContextProviders(entries);
        var memorySearchStages = CreateMemorySearchStages(entries);
        var latency = new GameAgentLatencyBreakdown(
            queue,
            ReadDouble(input, "inputPreparationMilliseconds"),
            ReadDouble(input, "sessionLoadMilliseconds"),
            ReadDouble(entries.LastOrDefault(value => value.Kind == "context.collected"), "durationMilliseconds"),
            ReadDouble(entries.LastOrDefault(value => value.Kind == "tools.collected"), "durationMilliseconds"),
            ReadDouble(entries.LastOrDefault(value => value.Kind == "skills.selected"), "durationMilliseconds"),
            firstResponse is null ? null : Milliseconds(executionStart, firstResponse.OperationalTimestamp),
            providerTimeToFirstResponse,
            messageEnded is null ? null : Milliseconds(executionStart, messageEnded.OperationalTimestamp),
            firstTool is null ? null : Milliseconds(executionStart, firstTool.OperationalTimestamp),
            modelRequestDuration,
            toolExecutionDuration,
            hostActionDuration,
            durableActionFrameworkDuration,
            approvalWaitDuration,
            Math.Max(0, executionDuration - modelRequestDuration - toolExecutionDuration - approvalWaitDuration),
            executionDuration,
            queue + executionDuration);
        return new GameAgentRunPerformance(
            sessionId,
            actorId,
            inputId,
            ReadString(messageEnded, "provider"),
            ReadString(messageEnded, "responseModel") ?? ReadString(messageEnded, "requestedModel"),
            ReadString(completed, "status") ?? "Unknown",
            latency,
            Array.AsReadOnly(tools),
            Array.AsReadOnly(contextProviders),
            Array.AsReadOnly(memorySearchStages),
            repeatEvents.Count(value => ReadString(value, "toolRepeatAction") == "Advisory"),
            repeatEvents.Count(value => ReadString(value, "toolRepeatAction") == "Terminated"),
            entries.Sum(ReadRetries),
            entries.Sum(ReadFallbacks),
            ReadInt64(usage, "totalTokens"),
            ReadBoolean(cost, "known", defaultValue: false),
            ReadNullableDouble(cost, "total"));
    }

    private static GameAgentContextProviderMetric[] CreateContextProviders(
        IReadOnlyList<GameAgentTraceEntry> entries) =>
        entries
            .Where(entry => entry.Kind == "context.provider.completed")
            .Select(entry => new GameAgentContextProviderMetric(
                ReadString(entry, "provider") ?? "unknown",
                ReadString(entry, "phase") ?? "unknown",
                ReadString(entry, "extensionId"),
                (int)ReadInt64(ReadDetails(entry), "sliceCount"),
                ReadDouble(entry, "durationMilliseconds")))
            .ToArray();

    private static GameAgentMemorySearchMetric[] CreateMemorySearchStages(
        IReadOnlyList<GameAgentTraceEntry> entries)
    {
        var metrics = new List<GameAgentMemorySearchMetric>();
        foreach (var entry in entries.Where(value => value.Kind == "memory.search.completed"))
        {
            var details = ReadDetails(entry);
            var source = ReadString(details, "source") ?? "unknown";
            if (!details.TryGetProperty("stages", out var stages) || stages.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var stage in stages.EnumerateArray().Take(64))
            {
                if (stage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                metrics.Add(new GameAgentMemorySearchMetric(
                    source,
                    ReadString(stage, "stage") ?? "Unknown",
                    ReadNullableDouble(stage, "durationMilliseconds") ?? 0,
                    (int)ReadInt64(stage, "scannedCount"),
                    (int)ReadInt64(stage, "candidateCount"),
                    ReadBoolean(stage, "reused", defaultValue: false)));
            }
        }

        return metrics.ToArray();
    }

    private static GameAgentToolMetric[] CreateTools(IReadOnlyList<GameAgentTraceEntry> entries)
    {
        var starts = new Dictionary<string, ToolStart>(StringComparer.Ordinal);
        var approvalWaits = entries
            .Where(value => value.Kind == "tool.approval.completed")
            .Select(value => new { Id = ReadString(value, "toolCallId"), Wait = ReadDouble(value, "waitMilliseconds") })
            .Where(value => value.Id is not null)
            .GroupBy(value => value.Id!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(value => value.Wait), StringComparer.Ordinal);
        var result = new List<GameAgentToolMetric>();
        string? provider = null;
        string? model = null;
        foreach (var entry in entries)
        {
            if (entry.Kind == "kernel.messageended")
            {
                provider = ReadString(entry, "provider") ?? provider;
                model = ReadString(entry, "responseModel") ?? ReadString(entry, "requestedModel") ?? model;
            }

            var id = ReadString(entry, "toolCallId");
            if (id is null)
            {
                continue;
            }

            if (entry.Kind == "kernel.toolstarted")
            {
                starts[id] = new ToolStart(entry, provider, model);
                continue;
            }

            if (entry.Kind != "kernel.toolended")
            {
                continue;
            }

            starts.TryGetValue(id, out var start);
            var action = ReadObject(entry, "action");
            var failed = ReadBoolean(entry, "toolError", defaultValue: true);
            result.Add(new GameAgentToolMetric(
                ReadString(entry, "tool") ?? ReadString(start?.Entry, "tool") ?? "unknown",
                start is null ? 0 : Milliseconds(start.Entry.OperationalTimestamp, entry.OperationalTimestamp),
                !failed,
                failed ? ReadString(entry, "failureCategory") ?? "Unspecified" : "None",
                ReadBoolean(entry, "outcomeUncertain", defaultValue: false),
                ReadString(entry, "operation"),
                start?.Provider,
                start?.Model,
                ReadString(action, "operationId"),
                ReadString(action, "status"),
                ReadNullableDouble(action, "hostMilliseconds")
                    ?? (action is null ? 0 : start is null ? 0 : Milliseconds(start.Entry.OperationalTimestamp, entry.OperationalTimestamp)),
                ReadNullableDouble(action, "frameworkMilliseconds") ?? 0,
                approvalWaits.TryGetValue(id, out var approvalWait) ? approvalWait : 0,
                ReadBoolean(action, "duplicateExecutionPrevented", defaultValue: false),
                ReadBoolean(action, "recovered", defaultValue: false)));
        }

        return result.ToArray();
    }

    private sealed class ToolStart
    {
        public ToolStart(GameAgentTraceEntry entry, string? provider, string? model)
        {
            Entry = entry;
            Provider = provider;
            Model = model;
        }

        public GameAgentTraceEntry Entry { get; }

        public string? Provider { get; }

        public string? Model { get; }
    }

    private static IReadOnlyList<ModelRequestMetric> CreateModelRequestDurations(IReadOnlyList<GameAgentTraceEntry> entries)
    {
        var starts = new Dictionary<string, GameAgentTraceEntry>(StringComparer.Ordinal);
        var firstResponses = new Dictionary<string, GameAgentTraceEntry>(StringComparer.Ordinal);
        var values = new List<ModelRequestMetric>();
        foreach (var entry in entries)
        {
            var runId = ReadString(entry, "runId");
            var turn = ReadInt64(ReadDetails(entry), "turn");
            if (runId is null || turn <= 0)
            {
                continue;
            }

            var key = runId + ":" + turn.ToString(CultureInfo.InvariantCulture);
            if (entry.Kind == "model.request.started")
            {
                starts[key] = entry;
            }
            else if (entry.Kind == "kernel.messagestarted" && starts.ContainsKey(key))
            {
                firstResponses[key] = entry;
            }
            else if (entry.Kind == "kernel.messageended" && starts.TryGetValue(key, out var start))
            {
                values.Add(new ModelRequestMetric(
                    Milliseconds(start.OperationalTimestamp, entry.OperationalTimestamp),
                    firstResponses.TryGetValue(key, out var firstResponse)
                        ? Milliseconds(start.OperationalTimestamp, firstResponse.OperationalTimestamp)
                        : (double?)null));
                starts.Remove(key);
                firstResponses.Remove(key);
            }
        }

        return values;
    }

    private sealed class ModelRequestMetric
    {
        public ModelRequestMetric(double durationMilliseconds, double? timeToFirstResponseMilliseconds)
        {
            DurationMilliseconds = durationMilliseconds;
            TimeToFirstResponseMilliseconds = timeToFirstResponseMilliseconds;
        }

        public double DurationMilliseconds { get; }

        public double? TimeToFirstResponseMilliseconds { get; }
    }

    private static int ReadRetries(GameAgentTraceEntry entry) =>
        (int)ReadInt64(ReadObject(ReadObject(entry, "providerAttempts"), "retry"), "retries");

    private static int ReadFallbacks(GameAgentTraceEntry entry) =>
        (int)ReadInt64(ReadObject(ReadObject(entry, "providerAttempts"), "fallback"), "fallbacks");

    private static JsonElement ReadDetails(GameAgentTraceEntry entry)
    {
        using var document = JsonDocument.Parse(entry.DetailsJson, new JsonDocumentOptions { MaxDepth = 128 });
        return document.RootElement.Clone();
    }

    private static JsonElement? ReadObject(GameAgentTraceEntry? entry, string name) =>
        entry is null ? null : ReadObject(ReadDetails(entry), name);

    private static JsonElement? ReadObject(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Object
            ? property
            : null;

    private static string? ReadString(GameAgentTraceEntry? entry, string name) =>
        entry is null ? null : ReadString(ReadDetails(entry), name);

    private static string? ReadString(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static double ReadDouble(GameAgentTraceEntry? entry, string name) =>
        entry is null ? 0 : ReadNullableDouble(ReadDetails(entry), name) ?? 0;

    private static double? ReadNullableDouble(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetDouble(out var number)
            ? number
            : null;

    private static long ReadInt64(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt64(out var number)
            ? number
            : 0;

    private static bool ReadBoolean(GameAgentTraceEntry? entry, string name, bool defaultValue) =>
        entry is null ? defaultValue : ReadBoolean(ReadDetails(entry), name, defaultValue);

    private static bool ReadBoolean(JsonElement? element, string name, bool defaultValue) =>
        element is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var property)
            ? property.ValueKind == JsonValueKind.True
                ? true
                : property.ValueKind == JsonValueKind.False
                    ? false
                    : defaultValue
            : defaultValue;

    private static double Milliseconds(DateTimeOffset start, DateTimeOffset end) =>
        Math.Max(0, (end - start).TotalMilliseconds);
}
