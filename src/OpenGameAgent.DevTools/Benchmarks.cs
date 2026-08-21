using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.DevTools;

public sealed class GameAgentBenchmarkScenario
{
    private readonly Func<int, CancellationToken, ValueTask<GameAgentTraceRecording>> _execute;

    public GameAgentBenchmarkScenario(
        string name,
        Func<int, CancellationToken, ValueTask<GameAgentTraceRecording>> execute)
    {
        Name = string.IsNullOrWhiteSpace(name) || name.Length > 256
            ? throw new ArgumentException("A bounded benchmark scenario name is required.", nameof(name))
            : name;
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public string Name { get; }

    internal ValueTask<GameAgentTraceRecording> ExecuteAsync(int iteration, CancellationToken cancellationToken) =>
        _execute(iteration, cancellationToken);
}

public sealed class GameAgentBenchmarkOptions
{
    public int Iterations { get; set; } = 10;
    public int WarmupIterations { get; set; } = 1;
    public int MaximumConcurrency { get; set; } = 1;
    public TimeSpan IterationTimeout { get; set; } = TimeSpan.FromMinutes(2);

    internal GameAgentBenchmarkOptions CopyAndValidate()
    {
        var copy = (GameAgentBenchmarkOptions)MemberwiseClone();
        if (copy.Iterations is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(Iterations));
        }

        if (copy.WarmupIterations is < 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(WarmupIterations));
        }

        if (copy.MaximumConcurrency is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrency));
        }

        if (copy.IterationTimeout < TimeSpan.FromMilliseconds(10) || copy.IterationTimeout > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(IterationTimeout));
        }

        return copy;
    }
}

public sealed class GameAgentBenchmarkThresholds
{
    public double? MaximumFailureRate { get; set; }
    public double? MaximumP95TotalMilliseconds { get; set; }
    public double? MaximumP95TimeToFirstResponseMilliseconds { get; set; }
    public double? MinimumToolSuccessRate { get; set; }
    public int? MaximumUncertainWrites { get; set; }

    internal void Validate()
    {
        RequireFraction(MaximumFailureRate, nameof(MaximumFailureRate));
        RequireFraction(MinimumToolSuccessRate, nameof(MinimumToolSuccessRate));
        RequireMilliseconds(MaximumP95TotalMilliseconds, nameof(MaximumP95TotalMilliseconds));
        RequireMilliseconds(MaximumP95TimeToFirstResponseMilliseconds, nameof(MaximumP95TimeToFirstResponseMilliseconds));
        if (MaximumUncertainWrites is < 0 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumUncertainWrites));
        }
    }

    private static void RequireFraction(double? value, string name)
    {
        if (value is { } number && (!double.IsFinite(number) || number < 0 || number > 1))
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    private static void RequireMilliseconds(double? value, string name)
    {
        if (value is { } number && (!double.IsFinite(number) || number < 0 || number > 86_400_000))
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

public sealed class GameAgentBenchmarkIteration
{
    internal GameAgentBenchmarkIteration(
        string scenario,
        int iteration,
        bool succeeded,
        double durationMilliseconds,
        string? errorType,
        GameAgentTraceRecording? recording)
    {
        Scenario = scenario;
        Iteration = iteration;
        Succeeded = succeeded;
        DurationMilliseconds = durationMilliseconds;
        ErrorType = errorType;
        Recording = recording;
    }

    public string Scenario { get; }
    public int Iteration { get; }
    public bool Succeeded { get; }
    public double DurationMilliseconds { get; }
    public string? ErrorType { get; }
    public GameAgentTraceRecording? Recording { get; }
}

public sealed class GameAgentBenchmarkReport
{
    internal GameAgentBenchmarkReport(
        IReadOnlyList<GameAgentBenchmarkIteration> iterations,
        GameAgentPerformanceSummary performance,
        IReadOnlyList<GameAgentTraceEvaluationFinding> findings)
    {
        Iterations = iterations;
        Performance = performance;
        Findings = findings;
    }

    public IReadOnlyList<GameAgentBenchmarkIteration> Iterations { get; }
    public GameAgentPerformanceSummary Performance { get; }
    public IReadOnlyList<GameAgentTraceEvaluationFinding> Findings { get; }
    public int Failures => Iterations.Count(value => !value.Succeeded);
    public double FailureRate => Iterations.Count == 0 ? 0 : (double)Failures / Iterations.Count;
    public double P50TotalMilliseconds => Percentile(Performance.Runs.Select(value => value.Latency.TotalMilliseconds), 0.50);
    public double P95TotalMilliseconds => Percentile(Performance.Runs.Select(value => value.Latency.TotalMilliseconds), 0.95);
    public double P95TimeToFirstResponseMilliseconds => Percentile(
        Performance.Runs.Select(value => value.Latency.TimeToFirstResponseMilliseconds).Where(value => value is not null).Select(value => value!.Value),
        0.95);
    public bool Passed => Findings.All(value => value.Passed || value.Severity == GameAgentTraceEvaluationSeverity.Information);

    public string ToJson() => JsonSerializer.Serialize(new
    {
        iterations = Iterations.Select(value => new
        {
            value.Scenario,
            value.Iteration,
            value.Succeeded,
            value.DurationMilliseconds,
            value.ErrorType,
        }),
        Failures,
        FailureRate,
        P50TotalMilliseconds,
        P95TotalMilliseconds,
        P95TimeToFirstResponseMilliseconds,
        Performance,
        Findings,
        Passed,
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });

    public string ToJsonLines() => string.Join(
        Environment.NewLine,
        Iterations.Select(value => JsonSerializer.Serialize(new
        {
            value.Scenario,
            value.Iteration,
            value.Succeeded,
            value.DurationMilliseconds,
            value.ErrorType,
            performance = value.Recording is null ? null : GameAgentPerformanceSummary.Create(value.Recording),
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })));

    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine($"Iterations: {Iterations.Count}; failures: {Failures} ({FailureRate:P2}); passed: {Passed}");
        text.AppendLine($"Total latency p50/p95: {P50TotalMilliseconds:0.###}/{P95TotalMilliseconds:0.###} ms");
        text.AppendLine($"TTFT p95: {P95TimeToFirstResponseMilliseconds:0.###} ms; tool success: {Performance.ToolSuccessRate:P2}");
        foreach (var finding in Findings)
        {
            text.AppendLine($"[{(finding.Passed ? "PASS" : "FAIL")}] {finding.RuleId}: {finding.Message}");
        }

        return text.ToString();
    }

    private static double Percentile(IEnumerable<double> source, double percentile)
    {
        var values = source.OrderBy(value => value).ToArray();
        if (values.Length == 0)
        {
            return 0;
        }

        var rank = (values.Length - 1) * percentile;
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return values[lower];
        }

        return values[lower] + ((values[upper] - values[lower]) * (rank - lower));
    }
}

public static class GameAgentBenchmarkRunner
{
    public static async Task<GameAgentBenchmarkReport> RunAsync(
        IEnumerable<GameAgentBenchmarkScenario> scenarios,
        GameAgentBenchmarkOptions? options = null,
        GameAgentBenchmarkThresholds? thresholds = null,
        CancellationToken cancellationToken = default)
    {
        var copied = (scenarios ?? throw new ArgumentNullException(nameof(scenarios))).ToArray();
        if (copied.Length is < 1 or > 1_000 || copied.Any(value => value is null))
        {
            throw new ArgumentException("Between 1 and 1,000 non-null benchmark scenarios are required.", nameof(scenarios));
        }

        if (copied.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != copied.Length)
        {
            throw new ArgumentException("Benchmark scenario names must be unique.", nameof(scenarios));
        }

        var configured = (options ?? new GameAgentBenchmarkOptions()).CopyAndValidate();
        var configuredThresholds = thresholds ?? new GameAgentBenchmarkThresholds();
        configuredThresholds.Validate();
        foreach (var scenario in copied)
        {
            for (var warmup = 0; warmup < configured.WarmupIterations; warmup++)
            {
                _ = await ExecuteAsync(scenario, -warmup - 1, configured.IterationTimeout, cancellationToken).ConfigureAwait(false);
            }
        }

        using var gate = new SemaphoreSlim(configured.MaximumConcurrency, configured.MaximumConcurrency);
        var tasks = copied.SelectMany(scenario => Enumerable.Range(0, configured.Iterations).Select(async iteration =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ExecuteAsync(scenario, iteration, configured.IterationTimeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ToArray();
        var iterations = await Task.WhenAll(tasks).ConfigureAwait(false);
        var ordered = iterations.OrderBy(value => value.Scenario, StringComparer.Ordinal).ThenBy(value => value.Iteration).ToArray();
        var recording = new GameAgentTraceRecording(ordered
            .Where(value => value.Recording is not null)
            .SelectMany(value => value.Recording!.Entries));
        var performance = GameAgentPerformanceSummary.Create(recording);
        var findings = Evaluate(ordered, performance, configuredThresholds);
        return new GameAgentBenchmarkReport(
            Array.AsReadOnly(ordered),
            performance,
            new ReadOnlyCollection<GameAgentTraceEvaluationFinding>(findings));
    }

    private static async Task<GameAgentBenchmarkIteration> ExecuteAsync(
        GameAgentBenchmarkScenario scenario,
        int iteration,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var recording = await scenario.ExecuteAsync(iteration, linked.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The benchmark scenario returned no trace recording.");
            return new GameAgentBenchmarkIteration(
                scenario.Name,
                iteration,
                succeeded: true,
                ElapsedMilliseconds(startedAt),
                errorType: null,
                recording);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GameAgentBenchmarkIteration(
                scenario.Name,
                iteration,
                succeeded: false,
                ElapsedMilliseconds(startedAt),
                "Timeout",
                recording: null);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new GameAgentBenchmarkIteration(
                scenario.Name,
                iteration,
                succeeded: false,
                ElapsedMilliseconds(startedAt),
                exception.GetType().Name,
                recording: null);
        }
    }

    private static List<GameAgentTraceEvaluationFinding> Evaluate(
        IReadOnlyList<GameAgentBenchmarkIteration> iterations,
        GameAgentPerformanceSummary performance,
        GameAgentBenchmarkThresholds thresholds)
    {
        var findings = new List<GameAgentTraceEvaluationFinding>();
        var failureRate = iterations.Count == 0 ? 0 : (double)iterations.Count(value => !value.Succeeded) / iterations.Count;
        AddMaximum(findings, "benchmark.failure-rate", failureRate, thresholds.MaximumFailureRate, "failure rate");
        AddMaximum(
            findings,
            "benchmark.total-latency-p95",
            Percentile(performance.Runs.Select(value => value.Latency.TotalMilliseconds), 0.95),
            thresholds.MaximumP95TotalMilliseconds,
            "p95 total latency in milliseconds");
        AddMaximum(
            findings,
            "benchmark.ttft-p95",
            Percentile(performance.Runs.Select(value => value.Latency.TimeToFirstResponseMilliseconds).Where(value => value is not null).Select(value => value!.Value), 0.95),
            thresholds.MaximumP95TimeToFirstResponseMilliseconds,
            "p95 time to first response in milliseconds");
        if (thresholds.MinimumToolSuccessRate is { } minimumToolSuccess)
        {
            findings.Add(new GameAgentTraceEvaluationFinding(
                "benchmark.tool-success-rate",
                performance.ToolSuccessRate >= minimumToolSuccess,
                $"Tool success rate was {performance.ToolSuccessRate.ToString("P3", CultureInfo.InvariantCulture)}; minimum is {minimumToolSuccess.ToString("P3", CultureInfo.InvariantCulture)}."));
        }

        if (thresholds.MaximumUncertainWrites is { } maximumUncertain)
        {
            findings.Add(new GameAgentTraceEvaluationFinding(
                "benchmark.uncertain-writes",
                performance.UncertainWrites <= maximumUncertain,
                $"Uncertain writes were {performance.UncertainWrites}; maximum is {maximumUncertain}."));
        }

        return findings;
    }

    private static void AddMaximum(
        ICollection<GameAgentTraceEvaluationFinding> findings,
        string rule,
        double actual,
        double? maximum,
        string label)
    {
        if (maximum is not { } limit)
        {
            return;
        }

        findings.Add(new GameAgentTraceEvaluationFinding(
            rule,
            actual <= limit,
            $"{label} was {actual.ToString("0.###", CultureInfo.InvariantCulture)}; maximum is {limit.ToString("0.###", CultureInfo.InvariantCulture)}."));
    }

    private static double Percentile(IEnumerable<double> source, double percentile)
    {
        var values = source.OrderBy(value => value).ToArray();
        if (values.Length == 0)
        {
            return 0;
        }

        var rank = (values.Length - 1) * percentile;
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return lower == upper
            ? values[lower]
            : values[lower] + ((values[upper] - values[lower]) * (rank - lower));
    }

    private static double ElapsedMilliseconds(long startedAt) =>
        (Stopwatch.GetTimestamp() - startedAt) * 1_000d / Stopwatch.Frequency;
}
