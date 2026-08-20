using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Extensions;

namespace OpenGameAgent.DevTools;

public sealed class GameAgentTraceRunSummary
{
    internal GameAgentTraceRunSummary(
        string sessionId,
        string actorId,
        string inputId,
        int entries,
        int toolCalls,
        int toolErrors,
        bool failed,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt)
    {
        SessionId = sessionId;
        ActorId = actorId;
        InputId = inputId;
        Entries = entries;
        ToolCalls = toolCalls;
        ToolErrors = toolErrors;
        Failed = failed;
        StartedAt = startedAt;
        EndedAt = endedAt;
    }

    public string SessionId { get; }
    public string ActorId { get; }
    public string InputId { get; }
    public int Entries { get; }
    public int ToolCalls { get; }
    public int ToolErrors { get; }
    public bool Failed { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset EndedAt { get; }
    public TimeSpan Duration => EndedAt - StartedAt;
}

public sealed class GameAgentTraceSummary
{
    internal GameAgentTraceSummary(
        int entries,
        int sessions,
        int actors,
        int failedRuns,
        int toolCalls,
        int toolErrors,
        DateTimeOffset? startedAt,
        DateTimeOffset? endedAt,
        IReadOnlyList<GameAgentTraceRunSummary> runs,
        IReadOnlyDictionary<string, int> eventKinds,
        IReadOnlyDictionary<string, int> tools)
    {
        Entries = entries;
        Sessions = sessions;
        Actors = actors;
        FailedRuns = failedRuns;
        ToolCalls = toolCalls;
        ToolErrors = toolErrors;
        StartedAt = startedAt;
        EndedAt = endedAt;
        Runs = runs;
        EventKinds = eventKinds;
        Tools = tools;
    }

    public int Entries { get; }
    public int Sessions { get; }
    public int Actors { get; }
    public int FailedRuns { get; }
    public int ToolCalls { get; }
    public int ToolErrors { get; }
    public DateTimeOffset? StartedAt { get; }
    public DateTimeOffset? EndedAt { get; }
    public TimeSpan Duration => StartedAt is null || EndedAt is null ? TimeSpan.Zero : EndedAt.Value - StartedAt.Value;
    public IReadOnlyList<GameAgentTraceRunSummary> Runs { get; }
    public IReadOnlyDictionary<string, int> EventKinds { get; }
    public IReadOnlyDictionary<string, int> Tools { get; }

    public static GameAgentTraceSummary Create(GameAgentTraceRecording recording)
    {
        if (recording is null)
        {
            throw new ArgumentNullException(nameof(recording));
        }

        var entries = recording.Entries;
        var kinds = Count(entries.Select(entry => entry.Kind));
        var toolNames = entries
            .Where(IsToolStarted)
            .Select(TryReadTool)
            .Where(value => value is not null)
            .Select(value => value!);
        var tools = Count(toolNames);
        var runs = entries
            .GroupBy(entry => new RunKey(entry.SessionId, entry.ActorId, entry.InputId))
            .Select(group => CreateRun(group.Key, group))
            .OrderBy(run => run.StartedAt)
            .ThenBy(run => run.SessionId, StringComparer.Ordinal)
            .ThenBy(run => run.ActorId, StringComparer.Ordinal)
            .ThenBy(run => run.InputId, StringComparer.Ordinal)
            .ToArray();

        return new GameAgentTraceSummary(
            entries.Count,
            entries.Select(entry => entry.SessionId).Distinct(StringComparer.Ordinal).Count(),
            entries.Select(entry => new ActorKey(entry.SessionId, entry.ActorId)).Distinct().Count(),
            runs.Count(run => run.Failed),
            runs.Sum(run => run.ToolCalls),
            runs.Sum(run => run.ToolErrors),
            entries.Count == 0 ? null : entries.Min(entry => entry.OperationalTimestamp),
            entries.Count == 0 ? null : entries.Max(entry => entry.OperationalTimestamp),
            Array.AsReadOnly(runs),
            new ReadOnlyDictionary<string, int>(kinds),
            new ReadOnlyDictionary<string, int>(tools));
    }

    internal static string? TryReadTool(GameAgentTraceEntry entry)
    {
        try
        {
            using var details = JsonDocument.Parse(entry.DetailsJson, new JsonDocumentOptions { MaxDepth = 128 });
            return details.RootElement.ValueKind == JsonValueKind.Object
                && details.RootElement.TryGetProperty("tool", out var tool)
                && tool.ValueKind == JsonValueKind.String
                    ? tool.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static GameAgentTraceRunSummary CreateRun(RunKey key, IEnumerable<GameAgentTraceEntry> values)
    {
        var entries = values.OrderBy(entry => entry.OperationalTimestamp).ThenBy(entry => entry.Sequence).ToArray();
        return new GameAgentTraceRunSummary(
            key.SessionId,
            key.ActorId,
            key.InputId,
            entries.Length,
            entries.Count(IsToolStarted),
            entries.Count(IsToolError),
            entries.Any(IsRunFailure),
            entries[0].OperationalTimestamp,
            entries[^1].OperationalTimestamp);
    }

    private static bool IsToolStarted(GameAgentTraceEntry entry) =>
        string.Equals(entry.Kind, "kernel.toolstarted", StringComparison.Ordinal);

    private static bool IsToolError(GameAgentTraceEntry entry)
    {
        if (!string.Equals(entry.Kind, "kernel.toolended", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var details = JsonDocument.Parse(entry.DetailsJson, new JsonDocumentOptions { MaxDepth = 128 });
            return details.RootElement.ValueKind == JsonValueKind.Object
                && details.RootElement.TryGetProperty("toolError", out var error)
                && error.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool IsRunFailure(GameAgentTraceEntry entry)
    {
        if (string.Equals(entry.Kind, "run.failed", StringComparison.Ordinal)
            || string.Equals(entry.Kind, "kernel.runfaulted", StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(entry.Kind, "run.completed", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var details = JsonDocument.Parse(entry.DetailsJson, new JsonDocumentOptions { MaxDepth = 128 });
            return details.RootElement.ValueKind != JsonValueKind.Object
                || !details.RootElement.TryGetProperty("succeeded", out var succeeded)
                || succeeded.ValueKind != JsonValueKind.True;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static Dictionary<string, int> Count(IEnumerable<string> values)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            result[value] = result.TryGetValue(value, out var count) ? checked(count + 1) : 1;
        }

        return result;
    }

    private readonly struct RunKey : IEquatable<RunKey>
    {
        public RunKey(string sessionId, string actorId, string inputId)
        {
            SessionId = sessionId;
            ActorId = actorId;
            InputId = inputId;
        }

        public string SessionId { get; }
        public string ActorId { get; }
        public string InputId { get; }

        public bool Equals(RunKey other) =>
            string.Equals(SessionId, other.SessionId, StringComparison.Ordinal)
            && string.Equals(ActorId, other.ActorId, StringComparison.Ordinal)
            && string.Equals(InputId, other.InputId, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is RunKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(SessionId);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(ActorId);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(InputId);
                return hash;
            }
        }
    }

    private readonly struct ActorKey : IEquatable<ActorKey>
    {
        public ActorKey(string sessionId, string actorId)
        {
            SessionId = sessionId;
            ActorId = actorId;
        }

        public string SessionId { get; }
        public string ActorId { get; }

        public bool Equals(ActorKey other) =>
            string.Equals(SessionId, other.SessionId, StringComparison.Ordinal)
            && string.Equals(ActorId, other.ActorId, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is ActorKey other && Equals(other);

        public override int GetHashCode() =>
            unchecked((StringComparer.Ordinal.GetHashCode(SessionId) * 397) ^ StringComparer.Ordinal.GetHashCode(ActorId));
    }
}

public sealed class GameAgentTraceEvaluationSpec
{
    private const int MaximumSpecCharacters = 1_000_000;
    private static readonly HashSet<string> SupportedProperties = new(StringComparer.Ordinal)
    {
        "maximumEntries",
        "maximumFailedRuns",
        "maximumToolCalls",
        "maximumToolErrors",
        "maximumRunDurationMilliseconds",
        "requiredEventKinds",
        "forbiddenEventKinds",
        "requiredTools",
        "forbiddenTools",
    };

    public int? MaximumEntries { get; set; }
    public int? MaximumFailedRuns { get; set; }
    public int? MaximumToolCalls { get; set; }
    public int? MaximumToolErrors { get; set; }
    public double? MaximumRunDurationMilliseconds { get; set; }
    public string[] RequiredEventKinds { get; set; } = Array.Empty<string>();
    public string[] ForbiddenEventKinds { get; set; } = Array.Empty<string>();
    public string[] RequiredTools { get; set; } = Array.Empty<string>();
    public string[] ForbiddenTools { get; set; } = Array.Empty<string>();

    public static GameAgentTraceEvaluationSpec FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumSpecCharacters)
        {
            throw new ArgumentException("An evaluation specification of at most 1,000,000 characters is required.", nameof(json));
        }

        using (var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 }))
        {
            ValidateDocument(document.RootElement);
        }

        var spec = JsonSerializer.Deserialize<GameAgentTraceEvaluationSpec>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
        }) ?? throw new JsonException("The evaluation specification is empty.");
        spec.Validate();
        return spec;
    }

    private static void ValidateDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("An evaluation specification must be a JSON object.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new JsonException($"Duplicate evaluation property '{property.Name}' is not allowed.");
            }

            if (!SupportedProperties.Contains(property.Name))
            {
                throw new JsonException($"Unknown evaluation property '{property.Name}'.");
            }
        }
    }

    internal void Validate()
    {
        ValidateLimit(MaximumEntries, nameof(MaximumEntries));
        ValidateLimit(MaximumFailedRuns, nameof(MaximumFailedRuns));
        ValidateLimit(MaximumToolCalls, nameof(MaximumToolCalls));
        ValidateLimit(MaximumToolErrors, nameof(MaximumToolErrors));
        if (MaximumRunDurationMilliseconds is { } duration
            && (double.IsNaN(duration) || double.IsInfinity(duration) || duration < 0 || duration > 86_400_000))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRunDurationMilliseconds));
        }

        RequiredEventKinds = ValidateNames(RequiredEventKinds, nameof(RequiredEventKinds));
        ForbiddenEventKinds = ValidateNames(ForbiddenEventKinds, nameof(ForbiddenEventKinds));
        RequiredTools = ValidateNames(RequiredTools, nameof(RequiredTools));
        ForbiddenTools = ValidateNames(ForbiddenTools, nameof(ForbiddenTools));
    }

    private static void ValidateLimit(int? value, string name)
    {
        if (value is < 0 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    private static string[] ValidateNames(string[]? values, string name)
    {
        var copy = values ?? Array.Empty<string>();
        if (copy.Length > 10_000
            || copy.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 1_024)
            || copy.Distinct(StringComparer.Ordinal).Count() != copy.Length)
        {
            throw new ArgumentException("Evaluation name lists must be unique, bounded, non-empty strings.", name);
        }

        return copy.ToArray();
    }
}

public enum GameAgentTraceEvaluationSeverity
{
    Information,
    Failure,
}

public sealed class GameAgentTraceEvaluationFinding
{
    public GameAgentTraceEvaluationFinding(
        string ruleId,
        bool passed,
        string message,
        GameAgentTraceEvaluationSeverity severity = GameAgentTraceEvaluationSeverity.Failure)
    {
        RuleId = Require(ruleId, nameof(ruleId));
        Message = Require(message, nameof(message), 16_384);
        Passed = passed;
        if (!Enum.IsDefined(typeof(GameAgentTraceEvaluationSeverity), severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity));
        }

        Severity = severity;
    }

    public string RuleId { get; }
    public bool Passed { get; }
    public string Message { get; }
    public GameAgentTraceEvaluationSeverity Severity { get; }

    private static string Require(string value, string name, int maximum = 256) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum
            ? throw new ArgumentException($"A non-empty value of at most {maximum} characters is required.", name)
            : value;
}

public interface IGameAgentTraceEvaluationRule
{
    string RuleId { get; }

    ValueTask<GameAgentTraceEvaluationFinding> EvaluateAsync(
        GameAgentTraceRecording recording,
        GameAgentTraceSummary summary,
        CancellationToken cancellationToken);
}

public sealed class GameAgentTraceEvaluationOptions
{
    public TimeSpan RuleTimeout { get; set; } = TimeSpan.FromSeconds(5);

    internal GameAgentTraceEvaluationOptions CopyAndValidate()
    {
        var copy = (GameAgentTraceEvaluationOptions)MemberwiseClone();
        if (copy.RuleTimeout < TimeSpan.FromMilliseconds(1) || copy.RuleTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(RuleTimeout));
        }

        return copy;
    }
}

public sealed class GameAgentTraceEvaluationReport
{
    internal GameAgentTraceEvaluationReport(
        GameAgentTraceSummary summary,
        IReadOnlyList<GameAgentTraceEvaluationFinding> findings)
    {
        Summary = summary;
        Findings = findings;
    }

    public GameAgentTraceSummary Summary { get; }
    public IReadOnlyList<GameAgentTraceEvaluationFinding> Findings { get; }
    public bool Passed => Findings.All(finding => finding.Passed || finding.Severity == GameAgentTraceEvaluationSeverity.Information);
}

public static class GameAgentTraceEvaluator
{
    public static async Task<GameAgentTraceEvaluationReport> EvaluateAsync(
        GameAgentTraceRecording recording,
        GameAgentTraceEvaluationSpec spec,
        IEnumerable<IGameAgentTraceEvaluationRule>? customRules = null,
        GameAgentTraceEvaluationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (recording is null)
        {
            throw new ArgumentNullException(nameof(recording));
        }

        if (spec is null)
        {
            throw new ArgumentNullException(nameof(spec));
        }

        spec.Validate();
        var validated = (options ?? new GameAgentTraceEvaluationOptions()).CopyAndValidate();
        var summary = GameAgentTraceSummary.Create(recording);
        var findings = EvaluateBuiltIns(summary, spec).ToList();
        var rules = (customRules ?? Array.Empty<IGameAgentTraceEvaluationRule>()).ToArray();
        if (rules.Length > 1_000 || rules.Any(rule => rule is null))
        {
            throw new ArgumentException("At most 1,000 non-null custom evaluation rules are supported.", nameof(customRules));
        }

        var ruleIds = rules.Select(rule => ValidateRuleId(rule.RuleId)).ToArray();
        if (ruleIds.Distinct(StringComparer.Ordinal).Count() != rules.Length)
        {
            throw new ArgumentException("Custom evaluation rule IDs must be unique.", nameof(customRules));
        }

        for (var index = 0; index < rules.Length; index++)
        {
            var rule = rules[index];
            var ruleId = ruleIds[index];
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(validated.RuleTimeout);
            try
            {
                var task = rule.EvaluateAsync(recording, summary, timeout.Token).AsTask();
                var finding = await WaitWithCancellationAsync(task, timeout.Token).ConfigureAwait(false);
                if (finding is null || !string.Equals(finding.RuleId, ruleId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A custom evaluation rule returned an invalid rule identity.");
                }

                findings.Add(finding);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                findings.Add(new GameAgentTraceEvaluationFinding(
                    ruleId,
                    passed: false,
                    "The custom evaluation rule exceeded its timeout."));
            }
            catch (Exception exception)
            {
                findings.Add(new GameAgentTraceEvaluationFinding(
                    ruleId,
                    passed: false,
                    $"The custom evaluation rule failed: {exception.GetType().Name}."));
            }
        }

        return new GameAgentTraceEvaluationReport(
            summary,
            new ReadOnlyCollection<GameAgentTraceEvaluationFinding>(findings));
    }

    private static string ValidateRuleId(string? ruleId)
    {
        if (string.IsNullOrWhiteSpace(ruleId) || ruleId.Length > 256)
        {
            throw new ArgumentException("Custom evaluation rule IDs must contain at most 256 non-whitespace characters.", nameof(ruleId));
        }

        return ruleId;
    }

    private static IEnumerable<GameAgentTraceEvaluationFinding> EvaluateBuiltIns(
        GameAgentTraceSummary summary,
        GameAgentTraceEvaluationSpec spec)
    {
        if (spec.MaximumEntries is { } maxEntries)
        {
            yield return Limit("entries.maximum", summary.Entries, maxEntries, "trace entries");
        }

        if (spec.MaximumFailedRuns is { } maxFailedRuns)
        {
            yield return Limit("runs.failed.maximum", summary.FailedRuns, maxFailedRuns, "failed runs");
        }

        if (spec.MaximumToolCalls is { } maxToolCalls)
        {
            yield return Limit("tools.calls.maximum", summary.ToolCalls, maxToolCalls, "tool calls");
        }

        if (spec.MaximumToolErrors is { } maxToolErrors)
        {
            yield return Limit("tools.errors.maximum", summary.ToolErrors, maxToolErrors, "tool errors");
        }

        if (spec.MaximumRunDurationMilliseconds is { } maxDuration)
        {
            var actual = summary.Runs.Count == 0 ? 0 : summary.Runs.Max(run => run.Duration.TotalMilliseconds);
            yield return new GameAgentTraceEvaluationFinding(
                "runs.duration.maximum",
                actual <= maxDuration,
                $"Maximum run duration was {actual:0.###} ms; limit is {maxDuration:0.###} ms.");
        }

        foreach (var value in spec.RequiredEventKinds)
        {
            yield return Required("events.required:" + value, summary.EventKinds.ContainsKey(value), "event kind", value);
        }

        foreach (var value in spec.ForbiddenEventKinds)
        {
            yield return Forbidden("events.forbidden:" + value, summary.EventKinds.ContainsKey(value), "event kind", value);
        }

        foreach (var value in spec.RequiredTools)
        {
            yield return Required("tools.required:" + value, summary.Tools.ContainsKey(value), "tool", value);
        }

        foreach (var value in spec.ForbiddenTools)
        {
            yield return Forbidden("tools.forbidden:" + value, summary.Tools.ContainsKey(value), "tool", value);
        }
    }

    private static GameAgentTraceEvaluationFinding Limit(string id, int actual, int maximum, string label) =>
        new(id, actual <= maximum, $"Observed {actual} {label}; limit is {maximum}.");

    private static GameAgentTraceEvaluationFinding Required(string id, bool found, string label, string value) =>
        new(id, found, found ? $"Required {label} '{value}' was observed." : $"Required {label} '{value}' was not observed.");

    private static GameAgentTraceEvaluationFinding Forbidden(string id, bool found, string label, string value) =>
        new(id, !found, found ? $"Forbidden {label} '{value}' was observed." : $"Forbidden {label} '{value}' was not observed.");

    private static async Task<T> WaitWithCancellationAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
        {
            return await task.ConfigureAwait(false);
        }

        var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancellation);
        if (task != await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false))
        {
            _ = task.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new OperationCanceledException(cancellationToken);
        }

        return await task.ConfigureAwait(false);
    }
}
