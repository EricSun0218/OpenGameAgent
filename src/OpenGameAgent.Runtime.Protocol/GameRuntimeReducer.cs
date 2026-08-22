using System.Collections.ObjectModel;

namespace OpenGameAgent.Runtime.Protocol;

public sealed class GameRuntimeReducer
{
    private readonly Dictionary<string, MutableItem> _items = new(StringComparer.Ordinal);
    private string? _sessionId;
    private string? _actorId;
    private string? _inputId;
    private string? _runId;
    private int? _turn;
    private GameRuntimeRunStatus _status = GameRuntimeRunStatus.Unknown;
    private long _lastSequence;
    private string? _resultJson;
    private bool _requiresTranscriptReconciliation;

    public GameRuntimeRunSnapshot Snapshot
    {
        get
        {
            if (_sessionId is null || _actorId is null || _inputId is null)
            {
                throw new InvalidOperationException("No runtime event has been applied.");
            }

            return new GameRuntimeRunSnapshot(
                _sessionId,
                _actorId,
                _inputId,
                _runId,
                _turn,
                _status,
                _lastSequence,
                _items.Values
                    .OrderBy(value => value.FirstSequence)
                    .Select(value => value.Snapshot)
                    .ToArray(),
                _resultJson,
                _requiresTranscriptReconciliation);
        }
    }

    public void Apply(GameRuntimeEventEnvelope value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (_lastSequence > 0 && value.Sequence != _lastSequence + 1)
        {
            throw new InvalidOperationException("Runtime events must be applied in contiguous sequence order.");
        }

        if (_sessionId is not null
            && (!string.Equals(_sessionId, value.SessionId, StringComparison.Ordinal)
                || !string.Equals(_actorId, value.ActorId, StringComparison.Ordinal)
                || !string.Equals(_inputId, value.InputId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("A reducer cannot combine different sessions or inputs.");
        }

        _sessionId = value.SessionId;
        _actorId = value.ActorId;
        _inputId = value.InputId;
        _lastSequence = value.Sequence;
        if (value.RunId is not null)
        {
            if (_runId is not null && !string.Equals(_runId, value.RunId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A reducer cannot combine different runs.");
            }

            _runId = value.RunId;
        }

        if (value.Turn is not null)
        {
            _turn = value.Turn;
        }

        switch (value.EventKind)
        {
            case GameRuntimeEventKind.Gap:
                _requiresTranscriptReconciliation = true;
                break;
            case GameRuntimeEventKind.Item:
                ApplyItem(value);
                break;
            case GameRuntimeEventKind.Run:
                ApplyRun(value);
                break;
            case GameRuntimeEventKind.Result:
                _resultJson = value.PayloadJson;
                if (_status is GameRuntimeRunStatus.Running or GameRuntimeRunStatus.Unknown)
                {
                    _status = GameRuntimeRunStatus.Completed;
                }

                break;
        }
    }

    private void ApplyItem(GameRuntimeEventEnvelope value)
    {
        var itemId = value.ItemId ?? throw new InvalidOperationException();
        if (!_items.TryGetValue(itemId, out var existing))
        {
            if (value.Lifecycle != GameRuntimeLifecycle.Started)
            {
                throw new InvalidOperationException("An item must start before it can receive delta or completion events.");
            }

            _items.Add(itemId, new MutableItem(value));
            return;
        }

        if (existing.Lifecycle == GameRuntimeLifecycle.Completed
            || value.Lifecycle == GameRuntimeLifecycle.Started
            || existing.Kind != value.ItemKind)
        {
            throw new InvalidOperationException("The runtime item lifecycle is invalid.");
        }

        existing.Update(value);
    }

    private void ApplyRun(GameRuntimeEventEnvelope value)
    {
        if (value.Lifecycle == GameRuntimeLifecycle.Started)
        {
            if (_status != GameRuntimeRunStatus.Unknown)
            {
                throw new InvalidOperationException("A run can start only once.");
            }

            _status = GameRuntimeRunStatus.Running;
            return;
        }

        if (value.Lifecycle == GameRuntimeLifecycle.Completed)
        {
            _status = value.Name switch
            {
                "run_completed" => GameRuntimeRunStatus.Completed,
                "run_stopped" => GameRuntimeRunStatus.Stopped,
                "run_aborted" => GameRuntimeRunStatus.Aborted,
                _ => GameRuntimeRunStatus.Failed,
            };

            if (_items.Values.Any(item => item.Lifecycle != GameRuntimeLifecycle.Completed))
            {
                _requiresTranscriptReconciliation = true;
            }
        }
    }

    private sealed class MutableItem
    {
        internal MutableItem(GameRuntimeEventEnvelope value)
        {
            ItemId = value.ItemId!;
            Kind = value.ItemKind!.Value;
            Lifecycle = value.Lifecycle;
            Name = value.Name;
            PayloadJson = value.PayloadJson;
            FirstSequence = value.Sequence;
            LastSequence = value.Sequence;
        }

        internal string ItemId { get; }
        internal GameRuntimeItemKind Kind { get; }
        internal GameRuntimeLifecycle Lifecycle { get; private set; }
        internal string Name { get; private set; }
        internal string PayloadJson { get; private set; }
        internal long FirstSequence { get; }
        internal long LastSequence { get; private set; }

        internal GameRuntimeItemSnapshot Snapshot => new(
            ItemId,
            Kind,
            Lifecycle,
            Name,
            PayloadJson,
            LastSequence);

        internal void Update(GameRuntimeEventEnvelope value)
        {
            Lifecycle = value.Lifecycle;
            Name = value.Name;
            PayloadJson = value.PayloadJson;
            LastSequence = value.Sequence;
        }
    }
}
