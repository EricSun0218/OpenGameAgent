using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace GameAgent.Core;

public static class GameTriggerCatchUpPolicies
{
    public const string All = "catch_up_all";
    public const string Once = "catch_up_once";
    public const string Skip = "skip";
    public const string Coalesce = "coalesce";

    internal static bool IsKnown(string value) =>
        value == All || value == Once || value == Skip || value == Coalesce;
}

public static class GameTriggerOverlapPolicies
{
    public const string Queue = "queue";
    public const string Skip = "skip";
    public const string Coalesce = "coalesce";
    public const string Replace = "replace";

    internal static bool IsKnown(string value) =>
        value == Queue || value == Skip || value == Coalesce || value == Replace;
}

public static class GameTriggerLaunchStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string CancelRequested = "cancel_requested";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsTerminal(string value) =>
        value == Completed || value == Failed || value == Cancelled;

    internal static bool IsKnown(string value) =>
        value == Queued
        || value == Running
        || value == CancelRequested
        || IsTerminal(value);
}

public sealed class GameTriggerDefinition
{
    public string TriggerId { get; set; } = string.Empty;

    public string ScopeKey { get; set; } = string.Empty;

    public string? ActorId { get; set; }

    public string CatchUpPolicy { get; set; } = GameTriggerCatchUpPolicies.All;

    public string OverlapPolicy { get; set; } = GameTriggerOverlapPolicies.Queue;

    public int MaxCatchUpOccurrences { get; set; } = 64;

    public int MaxRetainedLaunches { get; set; } = 256;
}

public sealed class GameTriggerOccurrence
{
    public string OccurrenceId { get; set; } = string.Empty;

    public long Sequence { get; set; }

    public GameTimePoint OccurredAt { get; set; } = null!;

    public JsonElement Payload { get; set; }
}

public sealed class GameTriggerLaunch
{
    public string LaunchId { get; set; } = string.Empty;

    public string TriggerId { get; set; } = string.Empty;

    public string ScopeKey { get; set; } = string.Empty;

    public string? ActorId { get; set; }

    public IReadOnlyList<string> OccurrenceIds { get; set; } = Array.Empty<string>();

    public long FirstSequence { get; set; }

    public long LastSequence { get; set; }

    public JsonElement Payload { get; set; }

    public string State { get; set; } = GameTriggerLaunchStates.Queued;

    public long Revision { get; set; }
}

public sealed class GameTriggerState
{
    public string StateKey { get; set; } = string.Empty;

    public string TriggerId { get; set; } = string.Empty;

    public string ScopeKey { get; set; } = string.Empty;

    public long LastOccurrenceSequence { get; set; }

    public IReadOnlyList<GameTriggerLaunch> Launches { get; set; } =
        Array.Empty<GameTriggerLaunch>();

    public long Revision { get; set; }
}

public interface IGameTriggerStateStore
{
    ValueTask<GameTriggerState?> TryGetAsync(
        string stateKey,
        CancellationToken cancellationToken);

    ValueTask PutAsync(
        GameTriggerState state,
        long? expectedRevision,
        CancellationToken cancellationToken);
}

public sealed class GameTriggerAdmission
{
    internal GameTriggerAdmission(
        GameTriggerState state,
        IReadOnlyList<GameTriggerLaunch> launches,
        IReadOnlyList<string> cancellationLaunchIds,
        IReadOnlyList<string> skippedOccurrenceIds,
        string? coalescedIntoLaunchId)
    {
        State = state;
        Launches = launches;
        CancellationLaunchIds = cancellationLaunchIds;
        SkippedOccurrenceIds = skippedOccurrenceIds;
        CoalescedIntoLaunchId = coalescedIntoLaunchId;
    }

    public GameTriggerState State { get; }

    public IReadOnlyList<GameTriggerLaunch> Launches { get; }

    public IReadOnlyList<string> CancellationLaunchIds { get; }

    public IReadOnlyList<string> SkippedOccurrenceIds { get; }

    public string? CoalescedIntoLaunchId { get; }
}

public sealed class GameTriggerException : Exception
{
    public GameTriggerException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

/// <summary>
/// Converts host-provided game-time occurrences into durable launches. It has
/// no wall-clock scheduler and never decides which game event should exist.
/// </summary>
public sealed class GameTriggerCoordinator
{
    private readonly IGameTriggerStateStore _store;
    private readonly JsonValueLimits _payloadLimits;

    public GameTriggerCoordinator(
        IGameTriggerStateStore store,
        int maxPayloadUtf8Bytes = 262_144)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (maxPayloadUtf8Bytes is < 1_024
            or > CanonicalJsonDigest.MaximumUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPayloadUtf8Bytes));
        }

        _payloadLimits = new JsonValueLimits(
            maxPayloadUtf8Bytes,
            maxDepth: 64,
            maxNodes: 65_536,
            maxStringUtf8Bytes: maxPayloadUtf8Bytes,
            maxContainerItems: 32_768);
    }

    public async ValueTask<GameTriggerAdmission> AdmitAsync(
        GameTriggerDefinition definition,
        IEnumerable<GameTriggerOccurrence> occurrences,
        CancellationToken cancellationToken = default)
    {
        var admittedDefinition = ValidateDefinition(definition);
        var input = SnapshotOccurrences(
            occurrences,
            admittedDefinition.MaxCatchUpOccurrences,
            cancellationToken);
        var stateKey = BuildStateKey(
            admittedDefinition.TriggerId,
            admittedDefinition.ScopeKey);
        for (var attempt = 0; attempt < 32; attempt++)
        {
            try
            {
                var current = await _store.TryGetAsync(stateKey, cancellationToken)
                    .ConfigureAwait(false);
                var expectedRevision = current?.Revision;
                var working = current is null
                    ? new GameTriggerState
                    {
                        StateKey = stateKey,
                        TriggerId = admittedDefinition.TriggerId,
                        ScopeKey = admittedDefinition.ScopeKey,
                        Revision = 0
                    }
                    : Snapshot(current);

                var newOccurrences = input
                    .Where(item => item.Sequence > working.LastOccurrenceSequence)
                    .OrderBy(item => item.Sequence)
                    .ToArray();
                if (newOccurrences.Length == 0)
                {
                    return new GameTriggerAdmission(
                        working,
                        Array.Empty<GameTriggerLaunch>(),
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        null);
                }

                working.LastOccurrenceSequence = newOccurrences[^1].Sequence;
                var selected = SelectOccurrences(admittedDefinition, newOccurrences);
                var skipped = new List<string>(newOccurrences
                    .Except(selected)
                    .Select(item => item.OccurrenceId));
                var launches = working.Launches.Select(Snapshot).ToList();
                var active = launches
                    .Where(item => !GameTriggerLaunchStates.IsTerminal(item.State))
                    .OrderBy(item => item.FirstSequence)
                    .ThenBy(item => item.LaunchId, StringComparer.Ordinal)
                    .ToArray();
                var cancellations = new List<string>();
                var admitted = new List<GameTriggerLaunch>();
                string? coalescedInto = null;
                var coalesceIntoNewLaunch = false;

                if (selected.Count != 0 && active.Length != 0)
                {
                    switch (admittedDefinition.OverlapPolicy)
                    {
                        case GameTriggerOverlapPolicies.Skip:
                            skipped.AddRange(selected.Select(item => item.OccurrenceId));
                            selected.Clear();
                            break;
                        case GameTriggerOverlapPolicies.Coalesce:
                            var target = active.FirstOrDefault(item =>
                                item.State == GameTriggerLaunchStates.Queued);
                            if (target is not null)
                            {
                                MergeInto(target, selected);
                                coalescedInto = target.LaunchId;
                                selected.Clear();
                            }
                            else
                            {
                                // A running launch has already consumed its input snapshot.
                                // Preserve new occurrences in one queued successor instead of
                                // mutating work the host can no longer observe.
                                coalesceIntoNewLaunch = true;
                            }

                            break;
                        case GameTriggerOverlapPolicies.Replace:
                            foreach (var launch in active)
                            {
                                if (launch.State != GameTriggerLaunchStates.CancelRequested)
                                {
                                    launch.State = GameTriggerLaunchStates.CancelRequested;
                                    launch.Revision++;
                                }

                                cancellations.Add(launch.LaunchId);
                            }

                            break;
                    }
                }

                if (selected.Count != 0)
                {
                    if (admittedDefinition.CatchUpPolicy == GameTriggerCatchUpPolicies.Coalesce
                        || coalesceIntoNewLaunch)
                    {
                        var launch = CreateLaunch(admittedDefinition, selected);
                        launches.Add(launch);
                        admitted.Add(Snapshot(launch));
                    }
                    else
                    {
                        foreach (var occurrence in selected)
                        {
                            var launch = CreateLaunch(
                                admittedDefinition,
                                new[] { occurrence });
                            launches.Add(launch);
                            admitted.Add(Snapshot(launch));
                        }
                    }
                }

                var nonTerminal = launches
                    .Where(item => !GameTriggerLaunchStates.IsTerminal(item.State))
                    .ToArray();
                if (nonTerminal.Length > admittedDefinition.MaxRetainedLaunches)
                {
                    throw new GameTriggerException(
                        "game_trigger_active_launch_limit",
                        "Active trigger launches reached the configured retention limit.");
                }

                var retainedTerminalCount = admittedDefinition.MaxRetainedLaunches
                                            - nonTerminal.Length;
                working.Launches = new ReadOnlyCollection<GameTriggerLaunch>(
                    nonTerminal
                        .Concat(launches
                            .Where(item => GameTriggerLaunchStates.IsTerminal(item.State))
                            .OrderByDescending(item => item.LastSequence)
                            .ThenBy(item => item.LaunchId, StringComparer.Ordinal)
                            .Take(retainedTerminalCount))
                        .OrderBy(item => item.FirstSequence)
                        .ThenBy(item => item.LaunchId, StringComparer.Ordinal)
                        .Select(Snapshot)
                        .ToArray());
                working.Revision++;
                await _store.PutAsync(working, expectedRevision, cancellationToken)
                    .ConfigureAwait(false);
                return new GameTriggerAdmission(
                    Snapshot(working),
                    new ReadOnlyCollection<GameTriggerLaunch>(admitted),
                    new ReadOnlyCollection<string>(cancellations),
                    new ReadOnlyCollection<string>(
                        skipped.Distinct(StringComparer.Ordinal).ToArray()),
                    coalescedInto);
            }
            catch (GameTriggerException exception)
                when (exception.ReasonCode == "game_trigger_revision_conflict")
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        throw new GameTriggerException(
            "game_trigger_contention",
            "The game trigger state changed too frequently to commit safely.");
    }

    public async ValueTask<GameTriggerLaunch> RecordLaunchStateAsync(
        string triggerId,
        string scopeKey,
        string launchId,
        string state,
        long expectedLaunchRevision,
        CancellationToken cancellationToken = default)
    {
        if (!GameTriggerLaunchStates.IsKnown(state))
        {
            throw new ArgumentException("The trigger launch state is invalid.", nameof(state));
        }

        var key = BuildStateKey(
            Required(triggerId, nameof(triggerId), 128),
            Required(scopeKey, nameof(scopeKey), 256));
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var current = await _store.TryGetAsync(key, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new GameTriggerException(
                    "game_trigger_state_missing",
                    "The game trigger state does not exist.");
            var launches = current.Launches.Select(Snapshot).ToList();
            var launch = launches.SingleOrDefault(item => item.LaunchId == launchId)
                ?? throw new GameTriggerException(
                    "game_trigger_launch_missing",
                    "The game trigger launch does not exist.");
            if (launch.State == state)
            {
                return Snapshot(launch);
            }

            if (launch.Revision != expectedLaunchRevision)
            {
                throw new GameTriggerException(
                    "game_trigger_revision_conflict",
                    "The game trigger launch revision changed.");
            }

            if (!CanTransition(launch.State, state))
            {
                throw new GameTriggerException(
                    "game_trigger_transition_invalid",
                    $"A trigger launch cannot transition from '{launch.State}' to '{state}'.");
            }

            launch.State = state;
            launch.Revision++;
            current.Launches = new ReadOnlyCollection<GameTriggerLaunch>(launches);
            var expectedStateRevision = current.Revision;
            current.Revision++;
            try
            {
                await _store.PutAsync(current, expectedStateRevision, cancellationToken)
                    .ConfigureAwait(false);
                return Snapshot(launch);
            }
            catch (GameTriggerException exception)
                when (exception.ReasonCode == "game_trigger_revision_conflict")
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        throw new GameTriggerException(
            "game_trigger_contention",
            "The game trigger state changed too frequently to record the launch state.");
    }

    private static bool CanTransition(string current, string next) => current switch
    {
        GameTriggerLaunchStates.Queued =>
            next == GameTriggerLaunchStates.Running
            || next == GameTriggerLaunchStates.CancelRequested
            || GameTriggerLaunchStates.IsTerminal(next),
        GameTriggerLaunchStates.Running =>
            next == GameTriggerLaunchStates.CancelRequested
            || GameTriggerLaunchStates.IsTerminal(next),
        GameTriggerLaunchStates.CancelRequested =>
            GameTriggerLaunchStates.IsTerminal(next),
        _ => false
    };

    private IReadOnlyList<GameTriggerOccurrence> SnapshotOccurrences(
        IEnumerable<GameTriggerOccurrence> occurrences,
        int maximum,
        CancellationToken cancellationToken)
    {
        if (occurrences is null)
        {
            throw new ArgumentNullException(nameof(occurrences));
        }

        var result = new List<GameTriggerOccurrence>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var sequences = new HashSet<long>();
        GameTimePoint? coordinate = null;
        foreach (var item in occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is null || result.Count >= maximum || item.OccurredAt is null)
            {
                throw new GameTriggerException(
                    "game_trigger_occurrence_limit",
                    "Game trigger occurrences are null or exceed the configured catch-up limit.");
            }

            if (item.Sequence < 1
                || !ids.Add(Required(item.OccurrenceId, nameof(item.OccurrenceId), 128))
                || !sequences.Add(item.Sequence))
            {
                throw new GameTriggerException(
                    "game_trigger_occurrence_invalid",
                    "Game trigger occurrence identity or sequence is invalid.");
            }

            coordinate ??= item.OccurredAt;
            if (!coordinate.IsComparableTo(item.OccurredAt))
            {
                throw new GameTriggerException(
                    "game_trigger_time_incompatible",
                    "Game trigger occurrences use incompatible game-time coordinates.");
            }

            JsonValueInspector.ValidateAndMeasure(
                item.Payload,
                _payloadLimits,
                nameof(item.Payload));
            result.Add(new GameTriggerOccurrence
            {
                OccurrenceId = item.OccurrenceId,
                Sequence = item.Sequence,
                OccurredAt = ExternalAttentionCoordinator.Clone(item.OccurredAt),
                Payload = item.Payload.Clone()
            });
        }

        return new ReadOnlyCollection<GameTriggerOccurrence>(result);
    }

    private static List<GameTriggerOccurrence> SelectOccurrences(
        GameTriggerDefinition definition,
        IReadOnlyList<GameTriggerOccurrence> occurrences)
    {
        return definition.CatchUpPolicy switch
        {
            GameTriggerCatchUpPolicies.Skip => new List<GameTriggerOccurrence>(),
            GameTriggerCatchUpPolicies.Once => new List<GameTriggerOccurrence>
            {
                occurrences[^1]
            },
            _ => occurrences.ToList()
        };
    }

    private static GameTriggerLaunch CreateLaunch(
        GameTriggerDefinition definition,
        IReadOnlyList<GameTriggerOccurrence> occurrences)
    {
        var ordered = occurrences.OrderBy(item => item.Sequence).ToArray();
        var ids = ordered.Select(item => item.OccurrenceId).ToArray();
        return new GameTriggerLaunch
        {
            LaunchId = "trigger:" + CanonicalJsonDigest.ComputeSha256(
                JsonArrayBuilder.Object(
                    ("triggerId", JsonArrayBuilder.String(definition.TriggerId)),
                    ("scopeKey", JsonArrayBuilder.String(definition.ScopeKey)),
                    ("occurrences", JsonArrayBuilder.Array(
                        ids.Select(JsonArrayBuilder.String).ToArray())))),
            TriggerId = definition.TriggerId,
            ScopeKey = definition.ScopeKey,
            ActorId = definition.ActorId,
            OccurrenceIds = new ReadOnlyCollection<string>(ids),
            FirstSequence = ordered[0].Sequence,
            LastSequence = ordered[^1].Sequence,
            Payload = ordered.Length == 1
                ? ordered[0].Payload.Clone()
                : JsonArrayBuilder.Object(
                    ("occurrences", JsonArrayBuilder.Array(
                        ordered.Select(item => JsonArrayBuilder.Object(
                            ("occurrenceId", JsonArrayBuilder.String(item.OccurrenceId)),
                            ("sequence", JsonArrayBuilder.Number(item.Sequence)),
                            ("payload", item.Payload.Clone()))).ToArray()))),
            State = GameTriggerLaunchStates.Queued,
            Revision = 1
        };
    }

    private static void MergeInto(
        GameTriggerLaunch launch,
        IReadOnlyList<GameTriggerOccurrence> occurrences)
    {
        var ids = launch.OccurrenceIds
            .Concat(occurrences.Select(item => item.OccurrenceId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        launch.OccurrenceIds = new ReadOnlyCollection<string>(ids);
        launch.LastSequence = Math.Max(
            launch.LastSequence,
            occurrences.Max(item => item.Sequence));
        launch.Payload = JsonArrayBuilder.Object(
            ("coalescedOccurrenceIds", JsonArrayBuilder.Array(
                ids.Select(JsonArrayBuilder.String).ToArray())));
        launch.Revision++;
    }

    private static GameTriggerDefinition ValidateDefinition(
        GameTriggerDefinition definition)
    {
        if (definition is null
            || !GameTriggerCatchUpPolicies.IsKnown(definition.CatchUpPolicy)
            || !GameTriggerOverlapPolicies.IsKnown(definition.OverlapPolicy)
            || definition.MaxCatchUpOccurrences is < 1 or > 4_096
            || definition.MaxRetainedLaunches is < 1 or > 65_536)
        {
            throw new ArgumentException("The game trigger definition is invalid.", nameof(definition));
        }

        return new GameTriggerDefinition
        {
            TriggerId = Required(definition.TriggerId, nameof(definition.TriggerId), 128),
            ScopeKey = Required(definition.ScopeKey, nameof(definition.ScopeKey), 256),
            ActorId = definition.ActorId is null
                ? null
                : Required(definition.ActorId, nameof(definition.ActorId), 128),
            CatchUpPolicy = definition.CatchUpPolicy,
            OverlapPolicy = definition.OverlapPolicy,
            MaxCatchUpOccurrences = definition.MaxCatchUpOccurrences,
            MaxRetainedLaunches = definition.MaxRetainedLaunches
        };
    }

    internal static GameTriggerState Snapshot(GameTriggerState state) =>
        new()
        {
            StateKey = state.StateKey,
            TriggerId = state.TriggerId,
            ScopeKey = state.ScopeKey,
            LastOccurrenceSequence = state.LastOccurrenceSequence,
            Launches = new ReadOnlyCollection<GameTriggerLaunch>(
                state.Launches.Select(Snapshot).ToArray()),
            Revision = state.Revision
        };

    internal static GameTriggerLaunch Snapshot(GameTriggerLaunch launch) =>
        new()
        {
            LaunchId = launch.LaunchId,
            TriggerId = launch.TriggerId,
            ScopeKey = launch.ScopeKey,
            ActorId = launch.ActorId,
            OccurrenceIds = new ReadOnlyCollection<string>(launch.OccurrenceIds.ToArray()),
            FirstSequence = launch.FirstSequence,
            LastSequence = launch.LastSequence,
            Payload = launch.Payload.Clone(),
            State = launch.State,
            Revision = launch.Revision
        };

    private static string BuildStateKey(string triggerId, string scopeKey) =>
        "trigger:" + CanonicalJsonDigest.ComputeSha256(JsonArrayBuilder.Object(
            ("triggerId", JsonArrayBuilder.String(triggerId)),
            ("scopeKey", JsonArrayBuilder.String(scopeKey))));

    private static string Required(string value, string name, int maximum) =>
        RuntimeGuard.RequiredUtf8(value, maximum, name);
}

public sealed class InMemoryGameTriggerStateStore : IGameTriggerStateStore
{
    private readonly int _maximumStates;
    private readonly ConcurrentDictionary<string, GameTriggerState> _states =
        new(StringComparer.Ordinal);

    public InMemoryGameTriggerStateStore(int maximumStates = 65_536)
    {
        if (maximumStates is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumStates));
        }

        _maximumStates = maximumStates;
    }

    public ValueTask<GameTriggerState?> TryGetAsync(
        string stateKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _states.TryGetValue(stateKey, out var state);
        return new ValueTask<GameTriggerState?>(
            state is null ? null : GameTriggerCoordinator.Snapshot(state));
    }

    public ValueTask PutAsync(
        GameTriggerState state,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = GameTriggerCoordinator.Snapshot(state);
        while (true)
        {
            if (_states.TryGetValue(snapshot.StateKey, out var current))
            {
                if (current.Revision != expectedRevision)
                {
                    throw new GameTriggerException(
                        "game_trigger_revision_conflict",
                        "The game trigger state revision changed.");
                }

                if (_states.TryUpdate(snapshot.StateKey, snapshot, current))
                {
                    return default;
                }

                continue;
            }

            if (expectedRevision is not null)
            {
                throw new GameTriggerException(
                    "game_trigger_revision_conflict",
                    "The expected game trigger state is missing.");
            }

            if (_states.Count >= _maximumStates)
            {
                throw new GameTriggerException(
                    "game_trigger_capacity",
                    "The game trigger state store is full.");
            }

            if (_states.TryAdd(snapshot.StateKey, snapshot))
            {
                return default;
            }
        }
    }
}
