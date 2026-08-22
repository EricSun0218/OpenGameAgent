using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Models;

public enum GameProviderConformanceSeverity
{
    Information,
    Warning,
    Error,
}

public sealed class GameProviderConformanceDiagnostic
{
    public GameProviderConformanceDiagnostic(
        GameProviderConformanceSeverity severity,
        string code,
        string message)
    {
        if (!Enum.IsDefined(typeof(GameProviderConformanceSeverity), severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity));
        }

        Severity = severity;
        Code = RequireBounded(code, nameof(code), 128);
        Message = RequireBounded(message, nameof(message), 2_048);
    }

    public GameProviderConformanceSeverity Severity { get; }

    public string Code { get; }

    public string Message { get; }

    private static string RequireBounded(string value, string name, int maximum) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)
            ? throw new ArgumentException("A bounded printable value is required.", name)
            : value;
}

public sealed class GameProviderConformanceOptions
{
    public int MaximumEvents { get; set; } = 4_096;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool RequireStartedEvent { get; set; } = true;

    public bool RequireProviderIdentity { get; set; }

    public IReadOnlyCollection<string> ForbiddenValues { get; set; } = Array.Empty<string>();

    internal GameProviderConformanceOptions CopyAndValidate()
    {
        if (MaximumEvents is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumEvents));
        }

        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        }

        var forbidden = (ForbiddenValues ?? throw new ArgumentNullException(nameof(ForbiddenValues)))
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (forbidden.Length > 64 || forbidden.Any(value => value.Length > 4_096))
        {
            throw new ArgumentException("Forbidden conformance values exceed their boundary.", nameof(ForbiddenValues));
        }

        return new GameProviderConformanceOptions
        {
            MaximumEvents = MaximumEvents,
            Timeout = Timeout,
            RequireStartedEvent = RequireStartedEvent,
            RequireProviderIdentity = RequireProviderIdentity,
            ForbiddenValues = Array.AsReadOnly(forbidden),
        };
    }
}

public sealed class GameProviderConformanceReport
{
    internal GameProviderConformanceReport(
        IReadOnlyList<GameProviderConformanceDiagnostic> diagnostics,
        IReadOnlyList<ModelStreamEventKind> eventKinds,
        ModelResponse? terminalResponse,
        TimeSpan duration,
        bool cancellationObserved)
    {
        Diagnostics = diagnostics;
        EventKinds = eventKinds;
        TerminalResponse = terminalResponse;
        Duration = duration;
        CancellationObserved = cancellationObserved;
    }

    public bool Passed => Diagnostics.All(value => value.Severity != GameProviderConformanceSeverity.Error);

    public IReadOnlyList<GameProviderConformanceDiagnostic> Diagnostics { get; }

    public IReadOnlyList<ModelStreamEventKind> EventKinds { get; }

    public ModelResponse? TerminalResponse { get; }

    public TimeSpan Duration { get; }

    public bool CancellationObserved { get; }
}

/// <summary>
/// Runs bounded, provider-neutral contract checks over a normalized model stream. Adapter authors
/// can pair this runner with a scripted transport fixture without depending on an Agent loop.
/// </summary>
public static class GameProviderConformance
{
    public static async ValueTask<GameProviderConformanceReport> RunAsync(
        IModelProvider provider,
        ModelRequest request,
        GameProviderConformanceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (provider is null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var settings = (options ?? new GameProviderConformanceOptions()).CopyAndValidate();
        var diagnostics = new List<GameProviderConformanceDiagnostic>();
        var eventKinds = new List<ModelStreamEventKind>();
        var activeBlocks = new Dictionary<int, ModelStreamEventKind>();
        ModelResponse? terminal = null;
        var terminalSeen = false;
        var startedCount = 0;
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);

        try
        {
            if (provider is IModelRequestPreflight preflight)
            {
                await preflight.ValidateRequestAsync(request, timeout.Token).ConfigureAwait(false);
            }

            var stream = provider.StreamAsync(request, timeout.Token)
                ?? throw new InvalidOperationException("The provider returned a null stream.");
            var enumerator = stream.GetAsyncEnumerator(timeout.Token);
            try
            {
                while (await MoveNextBoundedAsync(enumerator, timeout.Token).ConfigureAwait(false))
                {
                    var current = enumerator.Current;
                    if (current is null)
                    {
                        diagnostics.Add(Error("stream.null-event", "The provider yielded a null stream event."));
                        continue;
                    }

                    if (eventKinds.Count >= settings.MaximumEvents)
                    {
                        diagnostics.Add(Error("stream.event-limit", "The provider stream exceeded the configured event limit."));
                        break;
                    }

                    eventKinds.Add(current.Kind);
                    if (terminalSeen)
                    {
                        diagnostics.Add(Error("stream.after-terminal", "The provider yielded an event after its terminal event."));
                        continue;
                    }

                    if (current.Kind == ModelStreamEventKind.Started)
                    {
                        startedCount++;
                        if (eventKinds.Count != 1 || startedCount != 1)
                        {
                            diagnostics.Add(Error("stream.started-order", "Started must occur exactly once as the first event."));
                        }

                        continue;
                    }

                    if (current.IsTerminal)
                    {
                        terminalSeen = true;
                        terminal = current.Response;
                        if (terminal is null)
                        {
                            diagnostics.Add(Error("stream.missing-terminal-response", "A terminal event must carry a response."));
                        }
                        else
                        {
                            ValidateTerminal(current, terminal, settings, diagnostics);
                        }

                        if (activeBlocks.Count > 0)
                        {
                            diagnostics.Add(Error("stream.unclosed-content", "All content blocks must end before the terminal event."));
                        }

                        continue;
                    }

                    if (settings.RequireStartedEvent && startedCount == 0)
                    {
                        diagnostics.Add(Error("stream.missing-start", "A non-terminal event was emitted before Started."));
                    }

                    ValidateContentLifecycle(current, activeBlocks, diagnostics);
                }
            }
            finally
            {
                await DisposeBoundedAsync(enumerator, diagnostics, settings.ForbiddenValues).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            diagnostics.Add(Error("stream.timeout", "The provider did not finish within the configured timeout."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            diagnostics.Add(Error("stream.exception", BoundException(exception, settings.ForbiddenValues)));
        }

        stopwatch.Stop();
        if (settings.RequireStartedEvent && startedCount != 1)
        {
            diagnostics.Add(Error("stream.started-count", "The provider stream must contain exactly one Started event."));
        }

        if (!terminalSeen)
        {
            diagnostics.Add(Error("stream.missing-terminal", "The provider stream ended without a terminal event."));
        }

        return Report(diagnostics, eventKinds, terminal, stopwatch.Elapsed, false);
    }

    public static async ValueTask<GameProviderConformanceReport> RunCancellationProbeAsync(
        IModelProvider provider,
        ModelRequest blockingRequest,
        TimeSpan cancelAfter,
        GameProviderConformanceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (provider is null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        if (blockingRequest is null)
        {
            throw new ArgumentNullException(nameof(blockingRequest));
        }

        var settings = (options ?? new GameProviderConformanceOptions()).CopyAndValidate();
        if (cancelAfter <= TimeSpan.Zero || cancelAfter >= settings.Timeout)
        {
            throw new ArgumentOutOfRangeException(nameof(cancelAfter));
        }

        var diagnostics = new List<GameProviderConformanceDiagnostic>();
        var kinds = new List<ModelStreamEventKind>();
        var stopwatch = Stopwatch.StartNew();
        var observed = false;
        using var outer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        outer.CancelAfter(settings.Timeout);
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(outer.Token);
        try
        {
            var stream = provider.StreamAsync(blockingRequest, probe.Token)
                ?? throw new InvalidOperationException("The provider returned a null stream.");
            var enumerator = stream.GetAsyncEnumerator(probe.Token);
            try
            {
                var move = enumerator.MoveNextAsync().AsTask();
                await Task.Delay(cancelAfter, outer.Token).ConfigureAwait(false);
                probe.Cancel();
                var completed = await Task.WhenAny(move, Task.Delay(TimeSpan.FromSeconds(1), outer.Token))
                    .ConfigureAwait(false);
                if (completed != move)
                {
                    Observe(move);
                    diagnostics.Add(Error("cancellation.not-observed", "The provider did not settle within one second of cancellation."));
                }
                else
                {
                    var moved = await move.ConfigureAwait(false);
                    if (moved && enumerator.Current is not null)
                    {
                        kinds.Add(enumerator.Current.Kind);
                    }

                    diagnostics.Add(Error("cancellation.not-observed", "The blocking provider stream completed without cancellation."));
                }
            }
            finally
            {
                await DisposeBoundedAsync(enumerator, diagnostics, settings.ForbiddenValues).ConfigureAwait(false);
            }

        }
        catch (OperationCanceledException) when (probe.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            observed = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            diagnostics.Add(Error("cancellation.exception", BoundException(exception, settings.ForbiddenValues)));
        }

        stopwatch.Stop();
        return Report(diagnostics, kinds, null, stopwatch.Elapsed, observed);
    }

    private static void ValidateTerminal(
        ModelStreamEvent streamEvent,
        ModelResponse response,
        GameProviderConformanceOptions settings,
        ICollection<GameProviderConformanceDiagnostic> diagnostics)
    {
        var failed = response.StopReason is ModelStopReason.Error or ModelStopReason.Aborted;
        if (failed != (streamEvent.Kind == ModelStreamEventKind.Failed))
        {
            diagnostics.Add(Error("stream.terminal-kind", "The terminal event kind does not match the response stop reason."));
        }

        if (settings.RequireProviderIdentity
            && (string.IsNullOrWhiteSpace(response.Provider)
                || string.IsNullOrWhiteSpace(response.ResponseModel)))
        {
            diagnostics.Add(Error("response.identity", "The terminal response must identify its resolved provider and model."));
        }

        foreach (var forbidden in settings.ForbiddenValues)
        {
            if (Contains(response.ErrorMessage, forbidden)
                || Contains(response.Provider, forbidden)
                || Contains(response.Api, forbidden)
                || Contains(response.ResponseId, forbidden)
                || response.Diagnostics.Any(value =>
                    Contains(value.Code, forbidden)
                    || Contains(value.Message, forbidden)
                    || Contains(value.DataJson, forbidden)))
            {
                diagnostics.Add(Error("response.sensitive-value", "A terminal diagnostic exposed a forbidden value."));
                break;
            }
        }
    }

    private static void ValidateContentLifecycle(
        ModelStreamEvent value,
        IDictionary<int, ModelStreamEventKind> active,
        ICollection<GameProviderConformanceDiagnostic> diagnostics)
    {
        if (!TryGetLifecycle(value.Kind, out var family, out var phase))
        {
            return;
        }

        if (phase == 0)
        {
            if (active.ContainsKey(value.ContentIndex))
            {
                diagnostics.Add(Error("content.duplicate-start", "A content index started while another block at that index was active."));
            }
            else
            {
                active[value.ContentIndex] = family;
            }

            return;
        }

        if (!active.TryGetValue(value.ContentIndex, out var current) || current != family)
        {
            diagnostics.Add(Error("content.lifecycle", "A content delta or end did not match an active content block."));
            return;
        }

        if (phase == 2)
        {
            active.Remove(value.ContentIndex);
        }
    }

    private static bool TryGetLifecycle(ModelStreamEventKind value, out ModelStreamEventKind family, out int phase)
    {
        switch (value)
        {
            case ModelStreamEventKind.TextStarted:
            case ModelStreamEventKind.TextDelta:
            case ModelStreamEventKind.TextEnded:
                family = ModelStreamEventKind.TextStarted;
                phase = value == ModelStreamEventKind.TextStarted ? 0 : value == ModelStreamEventKind.TextDelta ? 1 : 2;
                return true;
            case ModelStreamEventKind.ReasoningStarted:
            case ModelStreamEventKind.ReasoningDelta:
            case ModelStreamEventKind.ReasoningEnded:
                family = ModelStreamEventKind.ReasoningStarted;
                phase = value == ModelStreamEventKind.ReasoningStarted ? 0 : value == ModelStreamEventKind.ReasoningDelta ? 1 : 2;
                return true;
            case ModelStreamEventKind.ToolCallStarted:
            case ModelStreamEventKind.ToolCallDelta:
            case ModelStreamEventKind.ToolCallEnded:
                family = ModelStreamEventKind.ToolCallStarted;
                phase = value == ModelStreamEventKind.ToolCallStarted ? 0 : value == ModelStreamEventKind.ToolCallDelta ? 1 : 2;
                return true;
            default:
                family = default;
                phase = -1;
                return false;
        }
    }

    private static bool Contains(string? value, string forbidden) =>
        value?.IndexOf(forbidden, StringComparison.Ordinal) >= 0;

    private static async ValueTask<bool> MoveNextBoundedAsync(
        IAsyncEnumerator<ModelStreamEvent> enumerator,
        CancellationToken cancellationToken)
    {
        var move = enumerator.MoveNextAsync().AsTask();
        var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(move, cancelled).ConfigureAwait(false);
        if (completed != move)
        {
            Observe(move);
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        }

        return await move.ConfigureAwait(false);
    }

    private static async ValueTask DisposeBoundedAsync(
        IAsyncEnumerator<ModelStreamEvent> enumerator,
        ICollection<GameProviderConformanceDiagnostic> diagnostics,
        IReadOnlyCollection<string> forbiddenValues)
    {
        Task dispose;
        try
        {
            dispose = enumerator.DisposeAsync().AsTask();
        }
        catch (Exception exception)
        {
            diagnostics.Add(Error("stream.dispose", BoundException(exception, forbiddenValues)));
            return;
        }

        var completed = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        if (completed != dispose)
        {
            Observe(dispose);
            diagnostics.Add(Error("stream.dispose-timeout", "The provider did not release its stream within one second."));
            return;
        }

        try
        {
            await dispose.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            diagnostics.Add(Error("stream.dispose", BoundException(exception, forbiddenValues)));
        }
    }

    private static void Observe(Task value) =>
        _ = value.ContinueWith(
            task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static string BoundException(Exception value, IReadOnlyCollection<string> forbiddenValues)
    {
        var message = new string((value.GetType().Name + ": " + value.Message)
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray());
        foreach (var forbidden in forbiddenValues)
        {
            message = message.Replace(forbidden, "[redacted]", StringComparison.Ordinal);
        }

        return message.Length <= 2_048 ? message : message.Substring(0, 2_048);
    }

    private static GameProviderConformanceDiagnostic Error(string code, string message) =>
        new(GameProviderConformanceSeverity.Error, code, message);

    private static GameProviderConformanceReport Report(
        IEnumerable<GameProviderConformanceDiagnostic> diagnostics,
        IEnumerable<ModelStreamEventKind> kinds,
        ModelResponse? terminal,
        TimeSpan duration,
        bool cancellationObserved) =>
        new(
            Array.AsReadOnly(diagnostics.ToArray()),
            Array.AsReadOnly(kinds.ToArray()),
            terminal,
            duration,
            cancellationObserved);
}

public static class GameProviderConformanceFixtures
{
    public static ModelRequest CreateTextRequest(string model = "conformance-model") =>
        Create(model, Array.Empty<ToolDefinition>(), "provider-conformance-text");

    public static ModelRequest CreateToolRequest(string model = "conformance-model") =>
        Create(
            model,
            new[]
            {
                new ToolDefinition(
                    "inspect_state",
                    "Read a bounded state key.",
                    "{\"type\":\"object\",\"properties\":{\"key\":{\"type\":\"string\"}},\"required\":[\"key\"],\"additionalProperties\":false}"),
            },
            "provider-conformance-tool");

    private static ModelRequest Create(string model, IReadOnlyList<ToolDefinition> tools, string runId) =>
        new(
            model,
            "Return a bounded conformance response.",
            new[]
            {
                new AgentMessage(
                    AgentRole.User,
                    new AgentContent[] { new TextContent("provider conformance") },
                    DateTimeOffset.UnixEpoch),
            },
            tools,
            new ModelParameters { MaxOutputTokens = 64, ReasoningLevel = "off" },
            "provider-conformance-session",
            runId,
            1);
}
