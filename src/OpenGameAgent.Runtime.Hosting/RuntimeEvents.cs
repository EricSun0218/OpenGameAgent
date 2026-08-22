using System.Security.Cryptography;
using System.Text;
using OpenGameAgent.Kernel;
using OpenGameAgent.Runtime.Protocol;

namespace OpenGameAgent.Runtime.Hosting;

public sealed class GameRuntimeEventDraft
{
    public GameRuntimeEventDraft(
        GameSessionKey key,
        string inputId,
        GameRuntimeEventKind eventKind,
        GameRuntimeLifecycle lifecycle,
        string name,
        string payloadJson,
        string? runId = null,
        int? turn = null,
        string? turnId = null,
        string? itemId = null,
        GameRuntimeItemKind? itemKind = null,
        bool terminal = false)
    {
        Key = ValidateKey(key);
        InputId = RequireId(inputId, nameof(inputId), 1_024);
        if (!Enum.IsDefined(typeof(GameRuntimeEventKind), eventKind)
            || !Enum.IsDefined(typeof(GameRuntimeLifecycle), lifecycle)
            || itemKind is { } typedItem && !Enum.IsDefined(typeof(GameRuntimeItemKind), typedItem))
        {
            throw new ArgumentOutOfRangeException(nameof(eventKind));
        }

        if (turn is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(turn));
        }

        EventKind = eventKind;
        Lifecycle = lifecycle;
        Name = RequireId(name, nameof(name), 256);
        PayloadJson = ValidateJson(payloadJson, nameof(payloadJson));
        RunId = runId is null ? null : RequireId(runId, nameof(runId), 1_024);
        Turn = turn;
        TurnId = turnId is null ? null : RequireId(turnId, nameof(turnId), 1_024);
        ItemId = itemId is null ? null : RequireId(itemId, nameof(itemId), 1_024);
        ItemKind = itemKind;
        Terminal = terminal;
        _ = new GameRuntimeEventEnvelope(
            GameRuntimeProtocol.Version,
            "validation-event",
            0,
            DateTimeOffset.UnixEpoch,
            Key.SessionId,
            Key.ActorId,
            InputId,
            EventKind,
            Lifecycle,
            Name,
            PayloadJson,
            RunId,
            Turn,
            TurnId,
            ItemId,
            ItemKind,
            Terminal);
    }

    public GameSessionKey Key { get; }
    public string InputId { get; }
    public string? RunId { get; }
    public int? Turn { get; }
    public string? TurnId { get; }
    public string? ItemId { get; }
    public GameRuntimeEventKind EventKind { get; }
    public GameRuntimeItemKind? ItemKind { get; }
    public GameRuntimeLifecycle Lifecycle { get; }
    public string Name { get; }
    public string PayloadJson { get; }
    public bool Terminal { get; }

    private static string RequireId(string value, string parameterName, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            throw new ArgumentException("A runtime identifier is required.", parameterName);
        }

        var id = value;
        return id.Length <= maximum
            ? id
            : throw new ArgumentException("The runtime identifier exceeds its boundary.", parameterName);
    }

    private static GameSessionKey ValidateKey(GameSessionKey key) =>
        new(key.SessionId, key.ActorId);

    private static string ValidateJson(string value, string parameterName)
    {
        try
        {
            return GameRuntimeJson.Deserialize<System.Text.Json.JsonElement>(value).GetRawText();
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or ArgumentException)
        {
            throw new ArgumentException("Valid bounded runtime JSON is required.", parameterName, exception);
        }
    }
}

public sealed class GameRuntimeAgentEventProjector
{
    private readonly GameSessionKey _key;
    private readonly string _inputId;
    private string? _runId;
    private string? _activeMessageItemId;
    private int _messageIndex;

    public GameRuntimeAgentEventProjector(GameSessionKey key, string inputId)
    {
        _key = new GameSessionKey(key.SessionId, key.ActorId);
        _inputId = string.IsNullOrWhiteSpace(inputId) || inputId.Length > 1_024 || inputId.Any(char.IsControl)
            ? throw new ArgumentException("A bounded input ID is required.", nameof(inputId))
            : inputId;
    }

    public GameRuntimeEventDraft Project(
        AgentEvent agentEvent,
        string payloadJson)
    {
        if (agentEvent is null)
        {
            throw new ArgumentNullException(nameof(agentEvent));
        }

        if (_runId is null)
        {
            _runId = agentEvent.RunId;
        }
        else if (!string.Equals(_runId, agentEvent.RunId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("One runtime event projector cannot combine multiple runs.");
        }

        var turn = agentEvent.Turn > 0 ? agentEvent.Turn : (int?)null;
        var turnId = turn is null ? null : GameRuntimeIds.Turn(agentEvent.RunId, turn.Value);
        return agentEvent.Kind switch
        {
            AgentEventKind.RunStarted => Draft(GameRuntimeEventKind.Run, GameRuntimeLifecycle.Started, "run_started"),
            AgentEventKind.TurnStarted => Draft(GameRuntimeEventKind.Turn, GameRuntimeLifecycle.Started, "turn_started"),
            AgentEventKind.ModelRequestStarted => Draft(GameRuntimeEventKind.Turn, GameRuntimeLifecycle.Delta, "model_request_started"),
            AgentEventKind.MessageStarted => Message(GameRuntimeLifecycle.Started, "message_started"),
            AgentEventKind.MessageUpdated => Message(GameRuntimeLifecycle.Delta, "message_delta"),
            AgentEventKind.MessageEnded => Message(GameRuntimeLifecycle.Completed, "message_completed"),
            AgentEventKind.ToolStarted => Item(GameRuntimeItemKind.Tool, GameRuntimeLifecycle.Started, "tool_started", ToolSource()),
            AgentEventKind.ToolProgressed => Item(GameRuntimeItemKind.Tool, GameRuntimeLifecycle.Delta, "tool_progress", ToolSource()),
            AgentEventKind.ToolEnded => Item(GameRuntimeItemKind.Tool, GameRuntimeLifecycle.Completed, "tool_completed", ToolSource()),
            AgentEventKind.TurnEnded => Draft(GameRuntimeEventKind.Turn, GameRuntimeLifecycle.Completed, "turn_completed"),
            AgentEventKind.RunFaulted => Draft(GameRuntimeEventKind.Run, GameRuntimeLifecycle.Delta, "run_faulted"),
            AgentEventKind.RunEnded => Draft(
                GameRuntimeEventKind.Run,
                GameRuntimeLifecycle.Completed,
                GameRuntimeEventProjection.RunName(agentEvent.Status)),
            _ => throw new ArgumentOutOfRangeException(nameof(agentEvent)),
        };

        string ToolSource() => agentEvent.ToolCall?.Id
            ?? throw new InvalidOperationException("A tool lifecycle event did not identify its tool call.");

        GameRuntimeEventDraft Draft(
            GameRuntimeEventKind eventKind,
            GameRuntimeLifecycle lifecycle,
            string name) => new(
            _key,
            _inputId,
            eventKind,
            lifecycle,
            name,
            payloadJson,
            agentEvent.RunId,
            turn,
            turnId);

        GameRuntimeEventDraft Item(
            GameRuntimeItemKind kind,
            GameRuntimeLifecycle lifecycle,
            string name,
            string sourceId) => new(
            _key,
            _inputId,
            GameRuntimeEventKind.Item,
            lifecycle,
            name,
            payloadJson,
            agentEvent.RunId,
            turn,
            turnId,
            GameRuntimeIds.Item(agentEvent.RunId, agentEvent.Turn, kind, sourceId),
            kind);

        GameRuntimeEventDraft Message(GameRuntimeLifecycle lifecycle, string name)
        {
            if (lifecycle == GameRuntimeLifecycle.Started)
            {
                if (_activeMessageItemId is not null)
                {
                    throw new InvalidOperationException("A message item started before the prior message completed.");
                }

                var role = agentEvent.Message?.Role.ToString() ?? "unknown";
                _activeMessageItemId = GameRuntimeIds.Item(
                    agentEvent.RunId,
                    agentEvent.Turn,
                    GameRuntimeItemKind.Message,
                    $"{checked(++_messageIndex)}:{role}");
            }
            else if (_activeMessageItemId is null)
            {
                throw new InvalidOperationException("A message delta or completion has no matching start event.");
            }

            var itemId = _activeMessageItemId;
            var value = new GameRuntimeEventDraft(
                _key,
                _inputId,
                GameRuntimeEventKind.Item,
                lifecycle,
                name,
                payloadJson,
                agentEvent.RunId,
                turn,
                turnId,
                itemId,
                GameRuntimeItemKind.Message);
            if (lifecycle == GameRuntimeLifecycle.Completed)
            {
                _activeMessageItemId = null;
            }

            return value;
        }
    }

}

public static class GameRuntimeEventProjection
{

    public static GameRuntimeEventDraft ProjectResult(
        GameInput input,
        GameAgentRunResult result,
        string payloadJson)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        return new GameRuntimeEventDraft(
            new GameSessionKey(input.SessionId, input.ActorId),
            input.InputId,
            GameRuntimeEventKind.Result,
            GameRuntimeLifecycle.Completed,
            result.Succeeded ? "result_completed" : "result_failed",
            payloadJson,
            result.AgentResult?.RunId,
            terminal: true);
    }

    internal static string RunName(AgentRunStatus? status) => status switch
    {
        AgentRunStatus.Completed => "run_completed",
        AgentRunStatus.Stopped => "run_stopped",
        AgentRunStatus.Aborted => "run_aborted",
        _ => "run_failed",
    };
}

public static class GameRuntimeIds
{
    public const string EventPrefix = GameRuntimeCursor.EventPrefix;
    public const string ItemPrefix = "oga-hi2-";
    public const string TurnPrefix = "oga-ht2-";

    public static string Event(GameSessionKey key, long sequence, string inputId)
    {
        key = new GameSessionKey(key.SessionId, key.ActorId);
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        return $"{EventPrefix}{sequence:x16}-{Digest(key.SessionId, key.ActorId, inputId, sequence.ToString())}";
    }

    public static bool TryReadEventSequence(string? eventId, out long sequence)
    {
        return GameRuntimeCursor.TryReadSequence(eventId, out sequence);
    }

    public static string Turn(string runId, int turn) =>
        $"{TurnPrefix}{Digest(runId, turn.ToString(System.Globalization.CultureInfo.InvariantCulture))}";

    public static string Item(
        string runId,
        int turn,
        GameRuntimeItemKind kind,
        string sourceId) =>
        $"{ItemPrefix}{Digest(
            runId,
            turn.ToString(System.Globalization.CultureInfo.InvariantCulture),
            kind.ToString(),
            sourceId)}";

    private static string Digest(params string[] values)
    {
        using var algorithm = SHA256.Create();
        var text = string.Join("\u001f", values);
        var hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(text));
        return string.Concat(hash.Take(16).Select(value => value.ToString("x2")));
    }
}
