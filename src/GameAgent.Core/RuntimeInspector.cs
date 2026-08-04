using System.Collections.ObjectModel;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class RuntimeInspectionQuery
{
    public long? AfterSequence { get; set; }

    public long? ThroughSequence { get; set; }

    public IReadOnlyList<string> EventKinds { get; set; } =
        Array.Empty<string>();

    public int MaxReturnedEvents { get; set; } = 1_000;

    public bool NewestFirst { get; set; }

    internal RuntimeInspectionQuery Snapshot()
    {
        if (AfterSequence < -1 || ThroughSequence < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AfterSequence),
                "Inspection sequence bounds are invalid.");
        }

        if (AfterSequence.HasValue
            && ThroughSequence.HasValue
            && AfterSequence.Value >= ThroughSequence.Value)
        {
            throw new ArgumentException(
                "AfterSequence must be smaller than ThroughSequence.");
        }

        if (MaxReturnedEvents is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxReturnedEvents));
        }

        var kinds = RuntimeInputGuard.CopyBounded(
            EventKinds,
            128,
            kind => RuntimeGuard.RequiredUtf8(
                kind,
                96,
                nameof(EventKinds)),
            nameof(EventKinds),
            "runtime_inspection_event_kinds_exceeded");
        if (kinds.Distinct(StringComparer.Ordinal).Count() != kinds.Length)
        {
            throw new ArgumentException(
                "Inspection event kinds cannot contain duplicates.",
                nameof(EventKinds));
        }

        return new RuntimeInspectionQuery
        {
            AfterSequence = AfterSequence,
            ThroughSequence = ThroughSequence,
            EventKinds = kinds.ToArray(),
            MaxReturnedEvents = MaxReturnedEvents,
            NewestFirst = NewestFirst
        };
    }
}

public sealed class RuntimeRunInspection
{
    internal RuntimeRunInspection(
        string runId,
        RuntimeTraceAnalysis analysis,
        IReadOnlyList<RuntimeEvent> events,
        int totalDurableEvents,
        bool truncated)
    {
        RunId = runId;
        Analysis = analysis;
        Events = events;
        TotalDurableEvents = totalDurableEvents;
        Truncated = truncated;
    }

    public string RunId { get; }

    public RuntimeTraceAnalysis Analysis { get; }

    public RuntimeRunProjection Summary => Analysis.Projection;

    public RuntimeTrajectory Trajectory => Analysis.Trajectory;

    public IReadOnlyList<RuntimeEvent> Events { get; }

    public int TotalDurableEvents { get; }

    public bool Truncated { get; }
}

/// <summary>
/// Read-only developer inspection surface over the authoritative run journal.
/// Analysis always sees the complete durable run; query filters only control
/// which cloned events are returned to the caller.
/// </summary>
public sealed class RuntimeInspector
{
    private readonly ISessionStore _store;
    private readonly RuntimeTraceAnalysisOptions _analysisOptions;
    private readonly RuntimeTraceExportOptions _exportOptions;

    public RuntimeInspector(
        ISessionStore store,
        RuntimeTraceAnalysisOptions? analysisOptions = null,
        RuntimeTraceExportOptions? exportOptions = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _analysisOptions = (analysisOptions
                            ?? new RuntimeTraceAnalysisOptions())
            .Snapshot();
        _exportOptions = (exportOptions ?? new RuntimeTraceExportOptions())
            .Snapshot();
    }

    public async ValueTask<RuntimeRunInspection> InspectAsync(
        string runId,
        RuntimeInspectionQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var boundedRunId = RuntimeGuard.RequiredUtf8(
            runId,
            128,
            nameof(runId));
        var snapshot = (query ?? new RuntimeInspectionQuery()).Snapshot();
        var events = await _store
            .ReadRunAsync(boundedRunId, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateStoreResult(boundedRunId, events);
        var analysis = new RuntimeTraceAnalyzer(_analysisOptions)
            .Analyze(events);
        var allowedKinds = snapshot.EventKinds.Count == 0
            ? null
            : new HashSet<string>(
                snapshot.EventKinds,
                StringComparer.Ordinal);
        IEnumerable<RuntimeEvent> filtered = events.Where(
            item => (!snapshot.AfterSequence.HasValue
                     || item.Sequence > snapshot.AfterSequence.Value)
                    && (!snapshot.ThroughSequence.HasValue
                        || item.Sequence <= snapshot.ThroughSequence.Value)
                    && (allowedKinds is null
                        || allowedKinds.Contains(item.Kind)));
        if (snapshot.NewestFirst)
        {
            filtered = filtered.Reverse();
        }

        var selected = new List<RuntimeEvent>(snapshot.MaxReturnedEvents);
        var truncated = false;
        using (var enumerator = filtered.GetEnumerator())
        {
            while (enumerator.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (selected.Count >= snapshot.MaxReturnedEvents)
                {
                    truncated = true;
                    break;
                }

                selected.Add(Clone(enumerator.Current));
            }
        }

        return new RuntimeRunInspection(
            boundedRunId,
            analysis,
            new ReadOnlyCollection<RuntimeEvent>(selected.ToArray()),
            events.Count,
            truncated);
    }

    public async ValueTask<RuntimeTraceExport> ExportAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        var boundedRunId = RuntimeGuard.RequiredUtf8(
            runId,
            128,
            nameof(runId));
        var events = await _store
            .ReadRunAsync(boundedRunId, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateStoreResult(boundedRunId, events);
        _ = new RuntimeTraceAnalyzer(_analysisOptions).Analyze(events);
        return new RuntimeTraceExporter(_exportOptions).Export(events);
    }

    private static void ValidateStoreResult(
        string runId,
        IReadOnlyList<RuntimeEvent> events)
    {
        if (events is null)
        {
            throw new InvalidDataException(
                "The session store returned a null run history.");
        }

        foreach (var runtimeEvent in events)
        {
            if (runtimeEvent is null
                || !string.Equals(
                    runtimeEvent.RunId,
                    runId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    runtimeEvent.Durability,
                    EventDurabilities.Durable,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The session store returned an invalid run history.");
            }
        }
    }

    private static RuntimeEvent Clone(RuntimeEvent runtimeEvent)
    {
        return ProtocolJson.DeserializeRuntimeEvent(
            ProtocolJson.Serialize(runtimeEvent));
    }
}
