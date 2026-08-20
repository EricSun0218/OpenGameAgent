using System.Text.Json;
using OpenGameAgent.DevTools;

namespace OpenGameAgent.DevTools.Cli;

public static class Program
{
    public static Task<int> Main(string[] args) => CommandLineApp.RunAsync(args, Console.Out, Console.Error);
}

public static class CommandLineApp
{
    private static readonly JsonSerializerOptions OutputJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            await standardOutput.WriteLineAsync(Usage()).ConfigureAwait(false);
            return 0;
        }

        if (args.Length > 64 || args.Any(value => value is null || value.Length > 32_768))
        {
            await standardError.WriteLineAsync("Invalid or excessive command-line arguments.").ConfigureAwait(false);
            return 1;
        }

        try
        {
            return args[0] switch
            {
                "inspect" => await InspectAsync(args[1..], standardOutput, cancellationToken).ConfigureAwait(false),
                "summarize" => await SummarizeAsync(args[1..], standardOutput, cancellationToken).ConfigureAwait(false),
                "evaluate" => await EvaluateAsync(args[1..], standardOutput, cancellationToken).ConfigureAwait(false),
                _ => await UnknownAsync(args[0], standardError).ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await standardError.WriteLineAsync("Operation canceled.").ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException
            or GameAgentTraceStorageException)
        {
            await standardError.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> InspectAsync(
        string[] args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var parsed = Parse(args, allowSpec: false, allowTitle: true, allowNoDetails: true);
        var recording = await GameAgentTraceRecordingReader.ReadAsync(parsed.Input!, cancellationToken: cancellationToken).ConfigureAwait(false);
        var target = parsed.Output ?? Path.ChangeExtension(Path.GetFullPath(parsed.Input!), ".trace.html");
        await GameAgentTraceHtmlReport.WriteAsync(
                recording,
                target,
                new GameAgentTraceHtmlReportOptions
                {
                    Title = parsed.Title ?? "OpenGameAgent Trace",
                    IncludeDetails = !parsed.NoDetails,
                },
                cancellationToken)
            .ConfigureAwait(false);
        await output.WriteLineAsync(Path.GetFullPath(target)).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> SummarizeAsync(
        string[] args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var parsed = Parse(args, allowSpec: false, allowTitle: false, allowNoDetails: false);
        var recording = await GameAgentTraceRecordingReader.ReadAsync(parsed.Input!, cancellationToken: cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(GameAgentTraceSummary.Create(recording), OutputJson);
        if (parsed.Output is null)
        {
            await output.WriteLineAsync(json).ConfigureAwait(false);
        }
        else
        {
            await WriteAtomicAsync(parsed.Output, json, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(Path.GetFullPath(parsed.Output)).ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<int> EvaluateAsync(
        string[] args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var parsed = Parse(args, allowSpec: true, allowTitle: false, allowNoDetails: false);
        if (parsed.Spec is null)
        {
            throw new ArgumentException("The evaluate command requires --spec <path>.");
        }

        var specInfo = new FileInfo(parsed.Spec);
        if (!specInfo.Exists || specInfo.Length > 1_000_000)
        {
            throw new ArgumentException("The evaluation specification must exist and contain at most 1,000,000 bytes.");
        }

        var recording = await GameAgentTraceRecordingReader.ReadAsync(parsed.Input!, cancellationToken: cancellationToken).ConfigureAwait(false);
        var spec = GameAgentTraceEvaluationSpec.FromJson(await File.ReadAllTextAsync(parsed.Spec, cancellationToken).ConfigureAwait(false));
        var report = await GameAgentTraceEvaluator.EvaluateAsync(recording, spec, cancellationToken: cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(report, OutputJson);
        if (parsed.Output is null)
        {
            await output.WriteLineAsync(json).ConfigureAwait(false);
        }
        else
        {
            await WriteAtomicAsync(parsed.Output, json, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(Path.GetFullPath(parsed.Output)).ConfigureAwait(false);
        }

        return report.Passed ? 0 : 2;
    }

    private static ParsedArguments Parse(
        string[] args,
        bool allowSpec,
        bool allowTitle,
        bool allowNoDetails)
    {
        string? input = null;
        string? output = null;
        string? spec = null;
        string? title = null;
        var noDetails = false;
        for (var index = 0; index < args.Length; index++)
        {
            var value = args[index];
            if (value == "--out")
            {
                output = Next(args, ref index, value, output);
            }
            else if (allowSpec && value == "--spec")
            {
                spec = Next(args, ref index, value, spec);
            }
            else if (allowTitle && value == "--title")
            {
                title = Next(args, ref index, value, title);
            }
            else if (allowNoDetails && value == "--no-details")
            {
                if (noDetails)
                {
                    throw new ArgumentException("--no-details may be supplied only once.");
                }

                noDetails = true;
            }
            else if (value.StartsWith("-", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unknown option '{value}'.");
            }
            else if (input is null)
            {
                input = value;
            }
            else
            {
                throw new ArgumentException("Only one input trace may be supplied.");
            }
        }

        if (input is null)
        {
            throw new ArgumentException("An input trace path is required.");
        }

        return new ParsedArguments(input, output, spec, title, noDetails);
    }

    private static string Next(string[] args, ref int index, string option, string? previous)
    {
        if (previous is not null)
        {
            throw new ArgumentException($"{option} may be supplied only once.");
        }

        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return args[index];
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, content, new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            if (File.Exists(fullPath))
            {
                File.Replace(temporary, fullPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, fullPath);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<int> UnknownAsync(string command, TextWriter error)
    {
        await error.WriteLineAsync($"Unknown command '{command}'.\n{Usage()}").ConfigureAwait(false);
        return 1;
    }

    private static string Usage() =>
        """
        OpenGameAgent DevTools

          inspect <trace.jsonl> [--out report.html] [--title text] [--no-details]
          summarize <trace.jsonl> [--out summary.json]
          evaluate <trace.jsonl> --spec evaluation.json [--out result.json]

        inspect performs observation-only playback. It never re-executes a model, tool, or game action.
        evaluate exits 0 when all rules pass, 2 when a rule fails, and 1 on invalid input.
        """;

    private sealed record ParsedArguments(
        string Input,
        string? Output,
        string? Spec,
        string? Title,
        bool NoDetails);
}
