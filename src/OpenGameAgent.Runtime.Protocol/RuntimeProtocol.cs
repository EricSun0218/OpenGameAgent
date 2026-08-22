using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenGameAgent.Runtime.Protocol;

public static class GameRuntimeProtocol
{
    public const int Version = 1;
    public const int MaximumJsonCharacters = 8_000_000;
    public const int MaximumPageSize = 1_024;

    public static IReadOnlyList<string> Capabilities { get; } = Array.AsReadOnly(new[]
    {
        "events.cursor.v1",
        "events.gap.v1",
        "items.lifecycle.v1",
        "runs.exact-control.v1",
        "runs.terminal-reconciliation.v1",
        "sessions.transcript-reconcile.v1",
    });
}

public static class GameRuntimeCursor
{
    public const string EventPrefix = "oga-rp1-";

    public static bool TryReadSequence(string? eventId, out long sequence)
    {
        sequence = 0;
        if (eventId is null
            || !eventId.StartsWith(EventPrefix, StringComparison.Ordinal)
            || eventId.Length < EventPrefix.Length + 17)
        {
            return false;
        }

        return long.TryParse(
                   eventId.Substring(EventPrefix.Length, 16),
                   System.Globalization.NumberStyles.AllowHexSpecifier,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out sequence)
               && sequence > 0;
    }
}

public enum GameRuntimeEventKind
{
    Run,
    Turn,
    Item,
    Result,
    Gap,
    Heartbeat,
}

public enum GameRuntimeItemKind
{
    Message,
    Tool,
    Action,
    Approval,
    Interaction,
    Artifact,
    Delegation,
    Plan,
    Media,
    Status,
}

public enum GameRuntimeLifecycle
{
    Started,
    Delta,
    Completed,
}

public enum GameRuntimeRunStatus
{
    Running,
    Completed,
    Stopped,
    Aborted,
    Failed,
    Unknown,
}

public enum GameRuntimeControlStatus
{
    Accepted,
    Idle,
    RunNotStarted,
    RunMismatch,
    TurnMismatch,
    ControlClosed,
    Unauthorized,
}

public sealed class GameRuntimeInitializeRequest
{
    [JsonConstructor]
    public GameRuntimeInitializeRequest(
        int minimumVersion = GameRuntimeProtocol.Version,
        int maximumVersion = GameRuntimeProtocol.Version,
        IReadOnlyList<string>? capabilities = null)
    {
        if (minimumVersion < 1 || maximumVersion < minimumVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumVersion));
        }

        MinimumVersion = minimumVersion;
        MaximumVersion = maximumVersion;
        Capabilities = CopyCapabilities(capabilities);
    }

    public int MinimumVersion { get; }

    public int MaximumVersion { get; }

    public IReadOnlyList<string> Capabilities { get; }

    private static IReadOnlyList<string> CopyCapabilities(IReadOnlyList<string>? values)
    {
        var copy = (values ?? Array.Empty<string>())
            .Select(value => GameRuntimeGuards.Id(value, nameof(values), 256))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (copy.Length > 256)
        {
            throw new ArgumentException("Too many runtime capabilities were requested.", nameof(values));
        }

        return Array.AsReadOnly(copy);
    }
}

public sealed class GameRuntimeInitializeResponse
{
    [JsonConstructor]
    public GameRuntimeInitializeResponse(
        int version,
        IReadOnlyList<string> capabilities,
        string serverName,
        string serverVersion)
    {
        if (version != GameRuntimeProtocol.Version)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        Version = version;
        Capabilities = Array.AsReadOnly((capabilities ?? throw new ArgumentNullException(nameof(capabilities)))
            .Select(value => GameRuntimeGuards.Id(value, nameof(capabilities), 256))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray());
        ServerName = GameRuntimeGuards.Id(serverName, nameof(serverName), 256);
        ServerVersion = GameRuntimeGuards.Id(serverVersion, nameof(serverVersion), 256);
    }

    public int Version { get; }

    public IReadOnlyList<string> Capabilities { get; }

    public string ServerName { get; }

    public string ServerVersion { get; }
}

public sealed class GameRuntimeStartRequest
{
    [JsonConstructor]
    public GameRuntimeStartRequest(string requestId, string inputJson)
    {
        RequestId = GameRuntimeGuards.Id(requestId, nameof(requestId), 1_024);
        InputJson = GameRuntimeGuards.Json(inputJson, nameof(inputJson));
    }

    public string RequestId { get; }

    public string InputJson { get; }
}

public sealed class GameRuntimeControlRequest
{
    [JsonConstructor]
    public GameRuntimeControlRequest(
        string sessionId,
        string actorId,
        string expectedRunId,
        string expectedTurnId,
        int expectedTurn,
        string? messageJson = null)
    {
        SessionId = GameRuntimeGuards.Id(sessionId, nameof(sessionId), 1_024);
        ActorId = GameRuntimeGuards.Id(actorId, nameof(actorId), 1_024);
        ExpectedRunId = GameRuntimeGuards.Id(expectedRunId, nameof(expectedRunId), 1_024);
        ExpectedTurnId = GameRuntimeGuards.Id(expectedTurnId, nameof(expectedTurnId), 1_024);
        if (expectedTurn < 1 || expectedTurn > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedTurn));
        }

        ExpectedTurn = expectedTurn;
        MessageJson = messageJson is null ? null : GameRuntimeGuards.Json(messageJson, nameof(messageJson));
    }

    public string SessionId { get; }

    public string ActorId { get; }

    public string ExpectedRunId { get; }

    public string ExpectedTurnId { get; }

    public int ExpectedTurn { get; }

    public string? MessageJson { get; }
}

public sealed class GameRuntimeReadEventsRequest
{
    [JsonConstructor]
    public GameRuntimeReadEventsRequest(
        string sessionId,
        string actorId,
        long afterSequence = 0,
        int maximum = 256)
    {
        SessionId = GameRuntimeGuards.Id(sessionId, nameof(sessionId), 1_024);
        ActorId = GameRuntimeGuards.Id(actorId, nameof(actorId), 1_024);
        if (afterSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        }

        if (maximum is < 1 or > GameRuntimeProtocol.MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        AfterSequence = afterSequence;
        Maximum = maximum;
    }

    public string SessionId { get; }

    public string ActorId { get; }

    public long AfterSequence { get; }

    public int Maximum { get; }
}

public sealed class GameRuntimeControlResponse
{
    [JsonConstructor]
    public GameRuntimeControlResponse(
        GameRuntimeControlStatus status,
        string? activeRunId = null,
        int? activeTurn = null)
    {
        if (!Enum.IsDefined(typeof(GameRuntimeControlStatus), status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (activeTurn is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(activeTurn));
        }

        Status = status;
        ActiveRunId = activeRunId is null ? null : GameRuntimeGuards.Id(activeRunId, nameof(activeRunId), 1_024);
        ActiveTurn = activeTurn;
        if ((ActiveRunId is null) != (ActiveTurn is null))
        {
            throw new ArgumentException("Active run ID and turn must be supplied together.");
        }
    }

    public GameRuntimeControlStatus Status { get; }

    public string? ActiveRunId { get; }

    public int? ActiveTurn { get; }

    public bool Accepted => Status == GameRuntimeControlStatus.Accepted;
}

public sealed class GameRuntimeEventEnvelope
{
    [JsonConstructor]
    public GameRuntimeEventEnvelope(
        int protocolVersion,
        string eventId,
        long sequence,
        DateTimeOffset occurredAt,
        string sessionId,
        string actorId,
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
        if (protocolVersion != GameRuntimeProtocol.Version)
        {
            throw new ArgumentOutOfRangeException(nameof(protocolVersion));
        }

        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

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

        ProtocolVersion = protocolVersion;
        EventId = GameRuntimeGuards.Id(eventId, nameof(eventId), 1_024);
        Sequence = sequence;
        OccurredAt = occurredAt;
        SessionId = GameRuntimeGuards.Id(sessionId, nameof(sessionId), 1_024);
        ActorId = GameRuntimeGuards.Id(actorId, nameof(actorId), 1_024);
        InputId = GameRuntimeGuards.Id(inputId, nameof(inputId), 1_024);
        EventKind = eventKind;
        Lifecycle = lifecycle;
        Name = GameRuntimeGuards.Id(name, nameof(name), 256);
        PayloadJson = GameRuntimeGuards.Json(payloadJson, nameof(payloadJson));
        RunId = runId is null ? null : GameRuntimeGuards.Id(runId, nameof(runId), 1_024);
        Turn = turn;
        TurnId = turnId is null ? null : GameRuntimeGuards.Id(turnId, nameof(turnId), 1_024);
        ItemId = itemId is null ? null : GameRuntimeGuards.Id(itemId, nameof(itemId), 1_024);
        ItemKind = itemKind;
        Terminal = terminal;
        ValidateShape();
    }

    public int ProtocolVersion { get; }

    public string EventId { get; }

    public long Sequence { get; }

    public DateTimeOffset OccurredAt { get; }

    public string SessionId { get; }

    public string ActorId { get; }

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

    private void ValidateShape()
    {
        if ((Turn is null) != (TurnId is null)
            || TurnId is not null && RunId is null
            || EventKind == GameRuntimeEventKind.Item && (ItemId is null || ItemKind is null)
            || EventKind != GameRuntimeEventKind.Item && (ItemId is not null || ItemKind is not null)
            || Terminal && Lifecycle != GameRuntimeLifecycle.Completed)
        {
            throw new ArgumentException("The runtime event coordinates are inconsistent.");
        }
    }
}

public sealed class GameRuntimeEventPage
{
    [JsonConstructor]
    public GameRuntimeEventPage(
        string sessionId,
        string actorId,
        long requestedAfterSequence,
        long firstRetainedSequence,
        long lastSequence,
        long nextAfterSequence,
        bool gap,
        IReadOnlyList<GameRuntimeEventEnvelope> events)
    {
        if (requestedAfterSequence < 0
            || firstRetainedSequence < 0
            || lastSequence < 0
            || nextAfterSequence < requestedAfterSequence
            || nextAfterSequence > Math.Max(lastSequence, requestedAfterSequence)
            || firstRetainedSequence > lastSequence + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedAfterSequence));
        }

        SessionId = GameRuntimeGuards.Id(sessionId, nameof(sessionId), 1_024);
        ActorId = GameRuntimeGuards.Id(actorId, nameof(actorId), 1_024);
        RequestedAfterSequence = requestedAfterSequence;
        FirstRetainedSequence = firstRetainedSequence;
        LastSequence = lastSequence;
        NextAfterSequence = nextAfterSequence;
        Gap = gap;
        var copy = (events ?? throw new ArgumentNullException(nameof(events))).ToArray();
        if (copy.Length > GameRuntimeProtocol.MaximumPageSize || copy.Any(value => value is null))
        {
            throw new ArgumentException("The runtime event page exceeds its boundary.", nameof(events));
        }

        Events = Array.AsReadOnly(copy);
    }

    public string SessionId { get; }

    public string ActorId { get; }

    public long RequestedAfterSequence { get; }

    public long FirstRetainedSequence { get; }

    public long LastSequence { get; }

    public bool Gap { get; }

    public IReadOnlyList<GameRuntimeEventEnvelope> Events { get; }

    /// <summary>
    /// The last retained sequence scanned by the server. It can be greater than the last visible
    /// event when audience projection removes private events, and remains safe as the next cursor.
    /// </summary>
    public long NextAfterSequence { get; }
}

public sealed class GameRuntimeItemSnapshot
{
    [JsonConstructor]
    public GameRuntimeItemSnapshot(
        string itemId,
        GameRuntimeItemKind kind,
        GameRuntimeLifecycle lifecycle,
        string name,
        string payloadJson,
        long lastSequence)
    {
        if (!Enum.IsDefined(typeof(GameRuntimeItemKind), kind)
            || !Enum.IsDefined(typeof(GameRuntimeLifecycle), lifecycle)
            || lastSequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ItemId = GameRuntimeGuards.Id(itemId, nameof(itemId), 1_024);
        Kind = kind;
        Lifecycle = lifecycle;
        Name = GameRuntimeGuards.Id(name, nameof(name), 256);
        PayloadJson = GameRuntimeGuards.Json(payloadJson, nameof(payloadJson));
        LastSequence = lastSequence;
    }

    public string ItemId { get; }

    public GameRuntimeItemKind Kind { get; }

    public GameRuntimeLifecycle Lifecycle { get; }

    public string Name { get; }

    public string PayloadJson { get; }

    public long LastSequence { get; }
}

public sealed class GameRuntimeRunSnapshot
{
    [JsonConstructor]
    public GameRuntimeRunSnapshot(
        string sessionId,
        string actorId,
        string inputId,
        string? runId,
        int? turn,
        GameRuntimeRunStatus status,
        long lastSequence,
        IReadOnlyList<GameRuntimeItemSnapshot> items,
        string? resultJson = null,
        bool requiresTranscriptReconciliation = false)
    {
        if (!Enum.IsDefined(typeof(GameRuntimeRunStatus), status)
            || lastSequence < 0
            || turn is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        SessionId = GameRuntimeGuards.Id(sessionId, nameof(sessionId), 1_024);
        ActorId = GameRuntimeGuards.Id(actorId, nameof(actorId), 1_024);
        InputId = GameRuntimeGuards.Id(inputId, nameof(inputId), 1_024);
        RunId = runId is null ? null : GameRuntimeGuards.Id(runId, nameof(runId), 1_024);
        Turn = turn;
        Status = status;
        LastSequence = lastSequence;
        var copy = (items ?? throw new ArgumentNullException(nameof(items))).ToArray();
        if (copy.Length > 100_000 || copy.Any(value => value is null))
        {
            throw new ArgumentException("The runtime snapshot contains too many items.", nameof(items));
        }

        Items = Array.AsReadOnly(copy);
        ResultJson = resultJson is null ? null : GameRuntimeGuards.Json(resultJson, nameof(resultJson));
        RequiresTranscriptReconciliation = requiresTranscriptReconciliation;
    }

    public string SessionId { get; }

    public string ActorId { get; }

    public string InputId { get; }

    public string? RunId { get; }

    public int? Turn { get; }

    public GameRuntimeRunStatus Status { get; }

    public long LastSequence { get; }

    public IReadOnlyList<GameRuntimeItemSnapshot> Items { get; }

    public string? ResultJson { get; }

    public bool RequiresTranscriptReconciliation { get; }
}

public static class GameRuntimeJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize<T>(T value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        var json = JsonSerializer.Serialize(value, Options);
        if (json.Length > GameRuntimeProtocol.MaximumJsonCharacters)
        {
            throw new InvalidOperationException("The runtime JSON exceeds its protocol boundary.");
        }

        return json;
    }

    public static T Deserialize<T>(string json)
    {
        GameRuntimeGuards.Json(json, nameof(json));
        using (var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 }))
        {
            RejectDuplicateProperties(document.RootElement);
        }

        return JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new JsonException("The runtime JSON did not contain a value.");
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException("Runtime JSON cannot contain duplicate properties.");
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            MaxDepth = 128,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

internal static class GameRuntimeGuards
{
    internal static string Id(string value, string parameterName, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumCharacters
            || value.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded non-control identifier is required.", parameterName);
        }

        return value;
    }

    internal static string Json(string value, string parameterName)
    {
        if (value is null || value.Length > GameRuntimeProtocol.MaximumJsonCharacters)
        {
            throw new ArgumentException("Runtime JSON is null or exceeds its boundary.", parameterName);
        }

        using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 128 });
        return document.RootElement.GetRawText();
    }
}
