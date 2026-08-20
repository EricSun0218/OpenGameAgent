using System.Text;
using System.Text.Json;
using OpenGameAgent.DevTools.Cli;
using OpenGameAgent.Extensions;
using Xunit;

namespace OpenGameAgent.DevTools.Tests;

public sealed class TraceDevToolsTests
{
    [Fact]
    public async Task JsonLinesSinkRoundTripsAndAppendKeepsCompleteEntries()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "trace.jsonl");
        await using (var sink = new JsonLinesGameAgentTraceSink(path))
        {
            await sink.WriteAsync(Entry(1, "input.received", "{}"), TestContext.Current.CancellationToken);
            await sink.WriteAsync(Entry(2, "kernel.toolstarted", "{\"tool\":\"move\"}"), TestContext.Current.CancellationToken);
        }

        await using (var sink = new JsonLinesGameAgentTraceSink(
            path,
            new GameAgentTraceFileOptions { Mode = GameAgentTraceFileMode.Append }))
        {
            await sink.WriteAsync(Entry(3, "run.completed", "{\"succeeded\":true}"), TestContext.Current.CancellationToken);
        }

        var recording = await GameAgentTraceRecordingReader.ReadAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, recording.Entries.Count);
        Assert.False(recording.IgnoredTruncatedFinalLine);
        Assert.Equal("move", GameAgentTraceSummary.Create(recording).Tools.Keys.Single());
        Assert.Equal(1, GameAgentTraceSummary.Create(recording).ToolCalls);
    }

    [Fact]
    public async Task CreateNewNeverOverwritesAnExistingRecording()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "trace.jsonl");
        await using (var sink = new JsonLinesGameAgentTraceSink(path))
        {
            await sink.WriteAsync(Entry(1, "input.received", "{}"), TestContext.Current.CancellationToken);
        }

        var exception = Assert.Throws<GameAgentTraceStorageException>(() => new JsonLinesGameAgentTraceSink(path));
        Assert.Equal(GameAgentTraceStorageError.AlreadyExists, exception.Code);
    }

    [Fact]
    public async Task ReaderIgnoresOnlyACrashTruncatedFinalLine()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "trace.jsonl");
        await using (var sink = new JsonLinesGameAgentTraceSink(path))
        {
            await sink.WriteAsync(Entry(1, "input.received", "{}"), TestContext.Current.CancellationToken);
        }

        await File.AppendAllTextAsync(path, "{\"schemaVersion\":1", TestContext.Current.CancellationToken);
        var recording = await GameAgentTraceRecordingReader.ReadAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(recording.Entries);
        Assert.True(recording.IgnoredTruncatedFinalLine);

        var strict = await Assert.ThrowsAsync<GameAgentTraceStorageException>(() =>
            GameAgentTraceRecordingReader.ReadAsync(
                path,
                new GameAgentTraceReadOptions { AllowTruncatedFinalLine = false },
                TestContext.Current.CancellationToken));
        Assert.Equal(GameAgentTraceStorageError.CorruptRecording, strict.Code);
    }

    [Fact]
    public async Task ReaderFailsClosedOnMiddleCorruptionAndEntryLimits()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "trace.jsonl");
        await using (var sink = new JsonLinesGameAgentTraceSink(path))
        {
            await sink.WriteAsync(Entry(1, "input.received", "{}"), TestContext.Current.CancellationToken);
            await sink.WriteAsync(Entry(2, "run.completed", "{\"succeeded\":true}"), TestContext.Current.CancellationToken);
        }

        var limited = await Assert.ThrowsAsync<GameAgentTraceStorageException>(() =>
            GameAgentTraceRecordingReader.ReadAsync(
                path,
                new GameAgentTraceReadOptions { MaximumEntries = 1 },
                TestContext.Current.CancellationToken));
        Assert.Equal(GameAgentTraceStorageError.LimitExceeded, limited.Code);

        var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        await File.WriteAllLinesAsync(path, new[] { lines[0], "not-json", lines[1] }, TestContext.Current.CancellationToken);
        var corrupt = await Assert.ThrowsAsync<GameAgentTraceStorageException>(() =>
            GameAgentTraceRecordingReader.ReadAsync(path, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GameAgentTraceStorageError.CorruptRecording, corrupt.Code);
    }

    [Fact]
    public async Task ConcurrentWritesRemainCompleteAndFileLimitFailsBeforeAnotherEntry()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "parallel.jsonl");
        await using (var sink = new JsonLinesGameAgentTraceSink(path))
        {
            var writes = Enumerable.Range(1, 200)
                .Select(index => sink.WriteAsync(Entry(index, "input.received", "{}"), TestContext.Current.CancellationToken).AsTask());
            await Task.WhenAll(writes);
        }

        var recording = await GameAgentTraceRecordingReader.ReadAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(200, recording.Entries.Count);
        Assert.Equal(200, recording.Entries.Select(entry => entry.Sequence).Distinct().Count());

        var limitedPath = Path.Combine(directory.Path, "limited.jsonl");
        await using var limited = new JsonLinesGameAgentTraceSink(
            limitedPath,
            new GameAgentTraceFileOptions { MaximumFileBytes = 512, MaximumLineBytes = 512 });
        await limited.WriteAsync(Entry(1, "input.received", "{}"), TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<GameAgentTraceStorageException>(async () =>
        {
            while (true)
            {
                await limited.WriteAsync(Entry(2, "input.received", "{}"), TestContext.Current.CancellationToken);
            }
        });
        Assert.Equal(GameAgentTraceStorageError.LimitExceeded, exception.Code);
    }

    [Fact]
    public async Task ReaderAcceptsValidUnterminatedTailAndRejectsInvalidEnvelopeFields()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "trace.jsonl");
        await using (var sink = new JsonLinesGameAgentTraceSink(path))
        {
            await sink.WriteAsync(Entry(1, "input.received", "{}"), TestContext.Current.CancellationToken);
        }

        var complete = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, complete.TrimEnd('\r', '\n'), TestContext.Current.CancellationToken);
        var valid = await GameAgentTraceRecordingReader.ReadAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(valid.Entries);
        Assert.False(valid.IgnoredTruncatedFinalLine);

        var line = complete.TrimEnd('\r', '\n');
        await File.WriteAllTextAsync(path, "{\"unexpected\":1," + line[1..] + Environment.NewLine, TestContext.Current.CancellationToken);
        var unknown = await Assert.ThrowsAsync<GameAgentTraceStorageException>(() =>
            GameAgentTraceRecordingReader.ReadAsync(path, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GameAgentTraceStorageError.CorruptRecording, unknown.Code);

        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1," + line[1..] + Environment.NewLine, TestContext.Current.CancellationToken);
        var duplicate = await Assert.ThrowsAsync<GameAgentTraceStorageException>(() =>
            GameAgentTraceRecordingReader.ReadAsync(path, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GameAgentTraceStorageError.CorruptRecording, duplicate.Code);

        await File.WriteAllTextAsync(
            path,
            line.Replace("\"tick\":42,", string.Empty, StringComparison.Ordinal) + Environment.NewLine,
            TestContext.Current.CancellationToken);
        var missing = await Assert.ThrowsAsync<GameAgentTraceStorageException>(() =>
            GameAgentTraceRecordingReader.ReadAsync(path, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GameAgentTraceStorageError.CorruptRecording, missing.Code);
    }

    [Fact]
    public async Task EvaluationSpecificationRejectsUnknownDuplicateAndNonFiniteValues()
    {
        Assert.Throws<JsonException>(() =>
            GameAgentTraceEvaluationSpec.FromJson("{\"maximumFailedRuns\":0,\"unknown\":1}"));
        Assert.Throws<JsonException>(() =>
            GameAgentTraceEvaluationSpec.FromJson("{\"maximumFailedRuns\":0,\"maximumFailedRuns\":1}"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => GameAgentTraceEvaluator.EvaluateAsync(Recording(Entry(1, "input.received", "{}")), new GameAgentTraceEvaluationSpec { MaximumRunDurationMilliseconds = double.NaN }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FailedAppendValidationReleasesTheFileHandle()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "incomplete.jsonl");
        File.WriteAllText(path, "{");

        var exception = Assert.Throws<GameAgentTraceStorageException>(() => new JsonLinesGameAgentTraceSink(
            path,
            new GameAgentTraceFileOptions { Mode = GameAgentTraceFileMode.Append }));
        Assert.Equal(GameAgentTraceStorageError.CorruptRecording, exception.Code);
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task EvaluatorSummarizesRunsToolsFailuresAndCiRules()
    {
        var recording = Recording(
            Entry(1, "input.received", "{}", inputId: "one", timestampOffsetMilliseconds: 0),
            Entry(2, "kernel.toolstarted", "{\"tool\":\"move\"}", inputId: "one", timestampOffsetMilliseconds: 10),
            Entry(3, "kernel.toolended", "{\"tool\":\"move\",\"toolError\":false}", inputId: "one", timestampOffsetMilliseconds: 20),
            Entry(4, "run.completed", "{\"succeeded\":true}", inputId: "one", timestampOffsetMilliseconds: 30),
            Entry(5, "input.received", "{}", inputId: "two", timestampOffsetMilliseconds: 40),
            Entry(6, "run.failed", "{\"message\":\"nope\"}", inputId: "two", timestampOffsetMilliseconds: 50));
        var spec = GameAgentTraceEvaluationSpec.FromJson(
            """{"maximumFailedRuns":0,"maximumToolCalls":2,"maximumToolErrors":0,"maximumRunDurationMilliseconds":50,"requiredEventKinds":["run.completed"],"requiredTools":["move"],"forbiddenTools":["delete_world"]}""");

        var report = await GameAgentTraceEvaluator.EvaluateAsync(recording, spec, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(report.Passed);
        Assert.Equal(2, report.Summary.Runs.Count);
        Assert.Equal(1, report.Summary.FailedRuns);
        Assert.Single(report.Findings, finding => finding.RuleId == "runs.failed.maximum" && !finding.Passed);
        Assert.Contains(report.Findings, finding => finding.RuleId == "tools.required:move" && finding.Passed);
    }

    [Fact]
    public async Task CustomRuleTimeoutBecomesAFindingInsteadOfHangingEvaluation()
    {
        var recording = Recording(Entry(1, "input.received", "{}"));
        var report = await GameAgentTraceEvaluator.EvaluateAsync(
            recording,
            new GameAgentTraceEvaluationSpec(),
            new[] { new NonCooperativeRule() },
            new GameAgentTraceEvaluationOptions { RuleTimeout = TimeSpan.FromMilliseconds(20) },
            TestContext.Current.CancellationToken);

        var finding = Assert.Single(report.Findings);
        Assert.False(finding.Passed);
        Assert.Contains("timeout", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CustomRuleCannotSpoofAnotherRuleIdentity()
    {
        var report = await GameAgentTraceEvaluator.EvaluateAsync(
            Recording(Entry(1, "input.received", "{}")),
            new GameAgentTraceEvaluationSpec(),
            new[] { new MismatchedRule() },
            cancellationToken: TestContext.Current.CancellationToken);

        var finding = Assert.Single(report.Findings);
        Assert.Equal("custom.declared", finding.RuleId);
        Assert.False(finding.Passed);
        Assert.Contains("InvalidOperationException", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlReportDoesNotEmbedUntrustedDetailsOrTitleAsMarkup()
    {
        var recording = Recording(Entry(1, "run.completed", "{\"succeeded\":true,\"text\":\"</script><img src=x onerror=alert(1)>\"}"));
        var html = GameAgentTraceHtmlReport.Create(
            recording,
            new GameAgentTraceHtmlReportOptions { Title = "<script>alert(1)</script>" });

        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x", html, StringComparison.Ordinal);
        Assert.Contains("Observation-only playback", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
        Assert.Contains("pageSize=500", html, StringComparison.Ordinal);
        Assert.Contains("id=\"previous\"", html, StringComparison.Ordinal);

        var placeholderTitle = GameAgentTraceHtmlReport.Create(
            recording,
            new GameAgentTraceHtmlReportOptions { Title = "__DATA__" });
        Assert.Contains("<title>__DATA__</title>", placeholderTitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HtmlReportAtomicallyReplacesAnExistingReport()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "report.html");
        var recording = Recording(Entry(1, "run.completed", "{\"succeeded\":true}"));

        await GameAgentTraceHtmlReport.WriteAsync(
            recording,
            path,
            new GameAgentTraceHtmlReportOptions { Title = "First" },
            TestContext.Current.CancellationToken);
        await GameAgentTraceHtmlReport.WriteAsync(
            recording,
            path,
            new GameAgentTraceHtmlReportOptions { Title = "Second" },
            TestContext.Current.CancellationToken);

        var html = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains("<title>Second</title>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<title>First</title>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliCreatesReportAndReturnsTwoForFailedEvaluation()
    {
        using var directory = new TemporaryDirectory();
        var trace = Path.Combine(directory.Path, "trace.jsonl");
        await using (var sink = new JsonLinesGameAgentTraceSink(trace))
        {
            await sink.WriteAsync(Entry(1, "run.failed", "{}"), TestContext.Current.CancellationToken);
        }

        var html = Path.Combine(directory.Path, "report.html");
        var output = new StringWriter();
        var error = new StringWriter();
        var inspect = await CommandLineApp.RunAsync(
            new[] { "inspect", trace, "--out", html },
            output,
            error,
            TestContext.Current.CancellationToken);
        Assert.Equal(0, inspect);
        Assert.True(File.Exists(html));

        var spec = Path.Combine(directory.Path, "spec.json");
        await File.WriteAllTextAsync(spec, "{\"maximumFailedRuns\":0}", TestContext.Current.CancellationToken);
        var evaluate = await CommandLineApp.RunAsync(
            new[] { "evaluate", trace, "--spec", spec },
            output,
            error,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, evaluate);

        var summary = Path.Combine(directory.Path, "summary.json");
        var summarize = await CommandLineApp.RunAsync(
            new[] { "summarize", trace, "--out", summary },
            output,
            error,
            TestContext.Current.CancellationToken);
        Assert.Equal(0, summarize);
        Assert.Contains(
            "\"failedRuns\": 1",
            await File.ReadAllTextAsync(summary, TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

        var help = await CommandLineApp.RunAsync(
            new[] { "help" },
            output,
            error,
            TestContext.Current.CancellationToken);
        Assert.Equal(0, help);
        Assert.Contains("observation-only", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static GameAgentTraceEntry Entry(
        long sequence,
        string kind,
        string details,
        string inputId = "input",
        int timestampOffsetMilliseconds = 0) =>
        new(
            sequence,
            kind,
            "session",
            "actor",
            inputId,
            new GameMoment("world", 42),
            new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(timestampOffsetMilliseconds),
            details);

    private static GameAgentTraceRecording Recording(params GameAgentTraceEntry[] entries) =>
        new(entries);

    private sealed class NonCooperativeRule : IGameAgentTraceEvaluationRule
    {
        private readonly TaskCompletionSource<GameAgentTraceEvaluationFinding> _never =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string RuleId => "custom.timeout";

        public ValueTask<GameAgentTraceEvaluationFinding> EvaluateAsync(
            GameAgentTraceRecording recording,
            GameAgentTraceSummary summary,
            CancellationToken cancellationToken) => new(_never.Task);
    }

    private sealed class MismatchedRule : IGameAgentTraceEvaluationRule
    {
        public string RuleId => "custom.declared";

        public ValueTask<GameAgentTraceEvaluationFinding> EvaluateAsync(
            GameAgentTraceRecording recording,
            GameAgentTraceSummary summary,
            CancellationToken cancellationToken) =>
            new(new GameAgentTraceEvaluationFinding("custom.spoofed", passed: true, "spoofed"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oga-devtools-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
