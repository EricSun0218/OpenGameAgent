using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Extensions;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GameGoalStatus
{
    Active,
    Waiting,
    Completed,
    Failed,
    Cancelled,
}

public sealed class GameGoalWaitCondition
{
    public GameGoalWaitCondition(
        string timelineId,
        long? notBeforeTick = null,
        IEnumerable<string>? eventTypes = null)
    {
        if (string.IsNullOrWhiteSpace(timelineId))
        {
            throw new ArgumentException("A timeline ID is required.", nameof(timelineId));
        }

        if (timelineId.Length > 1_024)
        {
            throw new ArgumentException("A timeline ID cannot exceed 1024 characters.", nameof(timelineId));
        }

        var copiedEventTypes = (eventTypes ?? Array.Empty<string>())
            .Select(value => string.IsNullOrWhiteSpace(value) || value.Length > 256
                ? throw new ArgumentException("A wait event type must contain 1 to 256 characters.", nameof(eventTypes))
                : value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (copiedEventTypes.Length > 32)
        {
            throw new ArgumentException("A wait condition can contain at most 32 event types.", nameof(eventTypes));
        }

        TimelineId = timelineId;
        NotBeforeTick = notBeforeTick;
        EventTypes = Array.AsReadOnly(copiedEventTypes);
    }

    public string TimelineId { get; }

    public long? NotBeforeTick { get; }

    public IReadOnlyList<string> EventTypes { get; }

    public bool IsSatisfied(GameInput input) =>
        string.Equals(TimelineId, input.Moment.TimelineId, StringComparison.Ordinal)
        && (NotBeforeTick is null || input.Moment.Tick >= NotBeforeTick.Value)
        && (EventTypes.Count == 0 || EventTypes.Contains(input.Type, StringComparer.Ordinal));
}

public sealed class GameGoalSnapshot
{
    internal GameGoalSnapshot(GoalDocument document)
    {
        Id = document.Id;
        ObjectiveJson = document.ObjectiveJson;
        ProgressJson = document.ProgressJson;
        Status = document.Status;
        Revision = document.Revision;
        NonProgressUpdates = document.NonProgressUpdates;
        TerminalSequence = document.TerminalSequence;
        LastTimelineId = document.LastTimelineId;
        LastTick = document.LastTick;
        Error = document.Error;
        Wait = document.Wait is null
            ? null
            : new GameGoalWaitCondition(document.Wait.TimelineId, document.Wait.NotBeforeTick, document.Wait.EventTypes);
    }

    public string Id { get; }

    public string ObjectiveJson { get; }

    public string ProgressJson { get; }

    public GameGoalStatus Status { get; }

    public long Revision { get; }

    public int NonProgressUpdates { get; }

    internal long TerminalSequence { get; }

    public string LastTimelineId { get; }

    public long LastTick { get; }

    public string? Error { get; }

    public GameGoalWaitCondition? Wait { get; }
}

public sealed class GameGoalChanged
{
    public GameGoalChanged(
        GameSessionKey session,
        string inputId,
        GameGoalSnapshot goal,
        string reason)
    {
        Session = new GameSessionKey(session.SessionId, session.ActorId);
        InputId = string.IsNullOrWhiteSpace(inputId) || inputId.Length > 1_024
            ? throw new ArgumentException("An input ID must contain 1 to 1024 characters.", nameof(inputId))
            : inputId;
        Goal = goal ?? throw new ArgumentNullException(nameof(goal));
        Reason = reason ?? string.Empty;
    }

    public GameSessionKey Session { get; }

    public string InputId { get; }

    public GameGoalSnapshot Goal { get; }

    public string Reason { get; }
}

public sealed class GameGoalQueryResult
{
    internal GameGoalQueryResult(
        GameSessionKey session,
        long sessionRevision,
        IEnumerable<GameGoalSnapshot> goals)
    {
        Session = new GameSessionKey(session.SessionId, session.ActorId);
        SessionRevision = sessionRevision >= 0
            ? sessionRevision
            : throw new ArgumentOutOfRangeException(nameof(sessionRevision));
        var copy = (goals ?? throw new ArgumentNullException(nameof(goals))).ToArray();
        if (copy.Any(goal => goal is null))
        {
            throw new ArgumentException("Goal query results cannot contain null goals.", nameof(goals));
        }

        Goals = Array.AsReadOnly(copy);
    }

    public GameSessionKey Session { get; }

    public long SessionRevision { get; }

    public IReadOnlyList<GameGoalSnapshot> Goals { get; }
}

public sealed class GoalLoopOptions
{
    public int MaximumActiveGoals { get; set; } = 64;

    public int MaximumRetainedTerminalGoals { get; set; } = 64;

    public int MaximumNonProgressUpdates { get; set; } = 3;

    internal GoalLoopOptions CopyAndValidate()
    {
        var copy = (GoalLoopOptions)MemberwiseClone();
        if (copy.MaximumActiveGoals < 1 || copy.MaximumActiveGoals > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumActiveGoals));
        }

        if (copy.MaximumRetainedTerminalGoals < 0 || copy.MaximumRetainedTerminalGoals > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRetainedTerminalGoals));
        }

        if (copy.MaximumNonProgressUpdates < 1 || copy.MaximumNonProgressUpdates > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumNonProgressUpdates));
        }

        return copy;
    }
}

public sealed class GoalLoopExtension : IGameAgentExtension
{
    private const string ExtensionId = "opengameagent.goals";
    private const string GoalPrefix = "goal/";
    private const string ManageSchema = """
        {
          "type":"object",
          "required":["action","goalId"],
          "properties":{
            "action":{"type":"string","enum":["create","progress","wait","complete","fail","cancel"]},
            "goalId":{"type":"string","minLength":1,"maxLength":128},
            "expectedRevision":{"type":"integer","minimum":0},
            "objective":{},
            "progress":{},
            "reason":{"type":"string","maxLength":4096},
            "notBeforeTick":{"type":"integer"},
            "eventTypes":{"type":"array","maxItems":32,"items":{"type":"string","minLength":1,"maxLength":256},"uniqueItems":true}
          },
          "additionalProperties":false
        }
        """;
    private const string ListSchema = """
        {"type":"object","properties":{"includeTerminal":{"type":"boolean"}},"additionalProperties":false}
        """;

    private readonly GoalLoopOptions _options;

    public GoalLoopExtension(GoalLoopOptions? options = null)
    {
        _options = (options ?? new GoalLoopOptions()).CopyAndValidate();
    }

    public static GameAgentExtensionChannel<GameGoalChanged> GoalChanged { get; } = new("goal.changed");

    public GameAgentExtensionDescriptor Descriptor { get; } = new(
        ExtensionId,
        "1.0.0",
        "Durable goal state that can wait on game time or game events and resume on later inputs.",
        new[] { "goals", "durable-loop", "game-time", "game-events" });

    public static async ValueTask<GameGoalQueryResult> ReadAsync(
        IGameSessionStore sessionStore,
        GameSessionKey session,
        bool includeTerminal = false,
        CancellationToken cancellationToken = default)
    {
        if (sessionStore is null)
        {
            throw new ArgumentNullException(nameof(sessionStore));
        }

        var key = new GameSessionKey(session.SessionId, session.ActorId);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await sessionStore.LoadAsync(key, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return new GameGoalQueryResult(key, 0, Array.Empty<GameGoalSnapshot>());
        }

        if (snapshot.Key != key)
        {
            throw new InvalidOperationException("The session store returned a different session key.");
        }

        var goals = ReadAll(StoredExtensionStateReader.Read(snapshot, ExtensionId))
            .Where(goal => includeTerminal || goal.Status is GameGoalStatus.Active or GameGoalStatus.Waiting)
            .OrderBy(goal => goal.Id, StringComparer.Ordinal)
            .ToArray();
        return new GameGoalQueryResult(key, snapshot.Revision, goals);
    }

    public void Configure(GameAgentExtensionApi api)
    {
        api.RegisterContextProvider(
            "goal-guidance",
            (context, _) => new ValueTask<IReadOnlyList<GameContextSlice>>(
                IsDirect(context.Input)
                    ? Array.Empty<GameContextSlice>()
                    : new[]
                    {
                        new GameContextSlice(
                            "goal-guidance",
                            JsonSerializer.Serialize(
                                "Use manage_goal for work that must survive this turn. Waiting goals must name game-time or game-event conditions; never use real-world time for narrative progress.")),
                    }));
        api.RegisterToolProvider(
            "goal-tools",
            (context, _) => new ValueTask<IReadOnlyList<AgentTool>>(
                IsDirect(context.Input)
                    ? Array.Empty<AgentTool>()
                    : new[]
                    {
                        CreateManageTool(api, context),
                        CreateListTool(context),
                    }));
        api.RegisterPendingWorkProvider(
            "active-goals",
            (context, cancellationToken) => ResumeAndCheckPendingAsync(api, context, cancellationToken),
            priority: 500);
    }

    private static bool IsDirect(GameInput input) =>
        input.Metadata.TryGetValue("agent.route", out var route)
        && string.Equals(route, "direct", StringComparison.OrdinalIgnoreCase);

    private async ValueTask<bool> ResumeAndCheckPendingAsync(
        GameAgentExtensionApi api,
        GameAgentExtensionRunContext context,
        CancellationToken cancellationToken)
    {
        PruneTerminalGoals(context.State);
        var pending = false;
        foreach (var storedGoal in ReadAll(context.State))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var goal = storedGoal;
            if (goal.Status == GameGoalStatus.Waiting
                && goal.Wait is not null
                && goal.Wait.IsSatisfied(context.Input))
            {
                var resumed = ToDocument(goal);
                resumed.Status = GameGoalStatus.Active;
                resumed.Wait = null;
                resumed.Revision = checked(resumed.Revision + 1);
                resumed.LastTimelineId = context.Input.Moment.TimelineId;
                resumed.LastTick = context.Input.Moment.Tick;
                Write(context.State, resumed);
                goal = new GameGoalSnapshot(resumed);
                await api.PublishAsync(
                    GoalChanged,
                    new GameGoalChanged(
                        new GameSessionKey(context.Input.SessionId, context.Input.ActorId),
                        context.Input.InputId,
                        goal,
                        "resumed"),
                    cancellationToken).ConfigureAwait(false);
            }

            pending |= goal.Status == GameGoalStatus.Active;
        }

        return pending;
    }

    private AgentTool CreateManageTool(GameAgentExtensionApi api, GameAgentExtensionRunContext context) =>
        new(
            new ToolDefinition(
                "manage_goal",
                "Create, update, wait, complete, fail, or cancel a durable game-agent goal.",
                ManageSchema),
            async (arguments, _, cancellationToken) =>
            {
                var action = arguments.GetProperty("action").GetString() ?? string.Empty;
                var goalId = arguments.GetProperty("goalId").GetString() ?? string.Empty;
                GoalDocument document;
                if (string.Equals(action, "create", StringComparison.Ordinal))
                {
                    if (Read(context.State, goalId) is not null)
                    {
                        return ToolResult.Error($"Goal '{goalId}' already exists.", ToolFailureCategory.Conflict);
                    }

                    var activeGoalCount = ReadAll(context.State).Count(goal => !IsTerminal(goal.Status));
                    if (activeGoalCount >= _options.MaximumActiveGoals)
                    {
                        return ToolResult.Error(
                            $"At most {_options.MaximumActiveGoals} active or waiting goals may exist in one actor session.",
                            ToolFailureCategory.RuleRejected);
                    }

                    if (!arguments.TryGetProperty("objective", out var objective))
                    {
                        return ToolResult.Error("Creating a goal requires objective JSON.", ToolFailureCategory.InvalidArguments);
                    }

                    document = new GoalDocument
                    {
                        Id = goalId,
                        ObjectiveJson = objective.GetRawText(),
                        ProgressJson = "{}",
                        Status = GameGoalStatus.Active,
                        Revision = 1,
                        LastTimelineId = context.Input.Moment.TimelineId,
                        LastTick = context.Input.Moment.Tick,
                    };
                }
                else
                {
                    var existing = Read(context.State, goalId);
                    if (existing is null)
                    {
                        return ToolResult.Error($"Goal '{goalId}' does not exist.", ToolFailureCategory.InvalidArguments);
                    }

                    document = existing;

                    if (document.Status is GameGoalStatus.Completed or GameGoalStatus.Failed or GameGoalStatus.Cancelled)
                    {
                        return ToolResult.Error($"Goal '{goalId}' is terminal and immutable.", ToolFailureCategory.Conflict);
                    }

                    if (!arguments.TryGetProperty("expectedRevision", out var revision)
                        || revision.GetInt64() != document.Revision)
                    {
                        return ToolResult.Error(
                            $"Goal '{goalId}' revision conflict. Current revision is {document.Revision}.",
                            ToolFailureCategory.Conflict);
                    }

                    document.Revision = checked(document.Revision + 1);
                    document.LastTimelineId = context.Input.Moment.TimelineId;
                    document.LastTick = context.Input.Moment.Tick;
                    switch (action)
                    {
                        case "progress":
                            if (!arguments.TryGetProperty("progress", out var progress))
                            {
                                return ToolResult.Error(
                                    "A progress update requires progress JSON.",
                                    ToolFailureCategory.InvalidArguments);
                            }

                            var nextProgress = progress.GetRawText();
                            document.NonProgressUpdates = string.Equals(document.ProgressJson, nextProgress, StringComparison.Ordinal)
                                ? checked(document.NonProgressUpdates + 1)
                                : 0;
                            if (document.NonProgressUpdates >= _options.MaximumNonProgressUpdates)
                            {
                                return ToolResult.Error(
                                    "The goal repeated the same progress without advancing.",
                                    ToolFailureCategory.RuleRejected);
                            }

                            document.ProgressJson = nextProgress;
                            document.Status = GameGoalStatus.Active;
                            document.Wait = null;
                            break;
                        case "wait":
                            var eventTypes = arguments.TryGetProperty("eventTypes", out var events)
                                ? events.EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray()
                                : Array.Empty<string>();
                            var notBeforeTick = arguments.TryGetProperty("notBeforeTick", out var tick)
                                ? tick.GetInt64()
                                : (long?)null;
                            if (notBeforeTick is null && eventTypes.Length == 0)
                            {
                                return ToolResult.Error(
                                    "A waiting goal requires a game tick or game event type.",
                                    ToolFailureCategory.InvalidArguments);
                            }

                            document.Status = GameGoalStatus.Waiting;
                            document.Wait = new GoalWaitDocument
                            {
                                TimelineId = context.Input.Moment.TimelineId,
                                NotBeforeTick = notBeforeTick,
                                EventTypes = eventTypes,
                            };
                            break;
                        case "complete":
                            document.Status = GameGoalStatus.Completed;
                            document.TerminalSequence = NextTerminalSequence(context.State);
                            document.Wait = null;
                            break;
                        case "fail":
                            document.Status = GameGoalStatus.Failed;
                            document.TerminalSequence = NextTerminalSequence(context.State);
                            document.Error = ReadReason(arguments, "The goal failed.");
                            document.Wait = null;
                            break;
                        case "cancel":
                            document.Status = GameGoalStatus.Cancelled;
                            document.TerminalSequence = NextTerminalSequence(context.State);
                            document.Error = ReadReason(arguments, "The goal was cancelled.");
                            document.Wait = null;
                            break;
                        default:
                            return ToolResult.Error(
                                $"Unsupported goal action '{action}'.",
                                ToolFailureCategory.InvalidArguments);
                    }
                }

                Write(context.State, document);
                if (IsTerminal(document.Status))
                {
                    PruneTerminalGoals(context.State);
                }
                var snapshot = new GameGoalSnapshot(document);
                await api.PublishAsync(
                    GoalChanged,
                    new GameGoalChanged(
                        new GameSessionKey(context.Input.SessionId, context.Input.ActorId),
                        context.Input.InputId,
                        snapshot,
                        action),
                    cancellationToken).ConfigureAwait(false);
                return JsonResult(snapshot);
            },
            ToolRisk.IdempotentWrite,
            ToolExecutionMode.Sequential,
            conflictKey: arguments => arguments.TryGetProperty("goalId", out var goalId)
                ? goalId.GetString()
                : null);

    private AgentTool CreateListTool(GameAgentExtensionRunContext context) =>
        new(
            new ToolDefinition("list_goals", "List durable goals for the current actor session.", ListSchema),
            (arguments, _, _) =>
            {
                var includeTerminal = arguments.TryGetProperty("includeTerminal", out var include) && include.GetBoolean();
                var goals = ReadAll(context.State)
                    .Where(goal => includeTerminal || goal.Status is GameGoalStatus.Active or GameGoalStatus.Waiting)
                    .OrderBy(goal => goal.Id, StringComparer.Ordinal)
                    .ToArray();
                return new ValueTask<ToolResult>(JsonResult(new { goals }));
            },
            ToolRisk.ReadOnly);

    private static string ReadReason(JsonElement arguments, string fallback) =>
        arguments.TryGetProperty("reason", out var reason) && !string.IsNullOrWhiteSpace(reason.GetString())
            ? reason.GetString()!
            : fallback;

    private static bool IsTerminal(GameGoalStatus status) =>
        status is GameGoalStatus.Completed or GameGoalStatus.Failed or GameGoalStatus.Cancelled;

    private static long NextTerminalSequence(GameAgentExtensionState state)
    {
        var maximum = ReadAll(state)
            .Where(goal => IsTerminal(goal.Status))
            .Select(goal => goal.TerminalSequence)
            .DefaultIfEmpty()
            .Max();
        return checked(maximum + 1);
    }

    private void PruneTerminalGoals(GameAgentExtensionState state)
    {
        var expired = ReadAll(state)
            .Where(goal => IsTerminal(goal.Status))
            .OrderByDescending(goal => goal.TerminalSequence)
            .ThenByDescending(goal => goal.LastTimelineId, StringComparer.Ordinal)
            .ThenByDescending(goal => goal.LastTick)
            .ThenBy(goal => goal.Id, StringComparer.Ordinal)
            .Skip(_options.MaximumRetainedTerminalGoals)
            .ToArray();
        foreach (var goal in expired)
        {
            state.Remove(GoalPrefix + goal.Id);
        }
    }

    private static GoalDocument? Read(GameAgentExtensionState state, string goalId)
    {
        var json = state.Get(GoalPrefix + goalId);
        return json is null
            ? null
            : Decode(json, goalId);
    }

    private static IReadOnlyList<GameGoalSnapshot> ReadAll(GameAgentExtensionState state)
        => ReadAll(state.Snapshot());

    private static IReadOnlyList<GameGoalSnapshot> ReadAll(IReadOnlyDictionary<string, string> state)
    {
        var goals = state
            .Where(pair => pair.Key.StartsWith(GoalPrefix, StringComparison.Ordinal))
            .Select(pair => Decode(pair.Value, pair.Key.Substring(GoalPrefix.Length)))
            .Select(document => new GameGoalSnapshot(document))
            .ToArray();
        var duplicate = goals.GroupBy(goal => goal.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Goal state contains duplicate ID '{duplicate.Key}'.");
        }

        return Array.AsReadOnly(goals);
    }

    private static GoalDocument Decode(string json, string expectedId)
    {
        GoalDocument document;
        try
        {
            document = JsonSerializer.Deserialize<GoalDocument>(json)
                ?? throw new InvalidOperationException("The goal document is null.");
            ValidateDocument(document, expectedId);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Goal '{expectedId}' state is invalid.", exception);
        }

        return document;
    }

    private static void ValidateDocument(GoalDocument document, string expectedId)
    {
        if (string.IsNullOrWhiteSpace(document.Id)
            || document.Id.Length > 128
            || !string.Equals(document.Id, expectedId, StringComparison.Ordinal)
            || document.Revision < 1
            || document.NonProgressUpdates < 0
            || document.TerminalSequence < 0
            || string.IsNullOrWhiteSpace(document.LastTimelineId)
            || !Enum.IsDefined(typeof(GameGoalStatus), document.Status)
            || (document.Error?.Length ?? 0) > 4_096)
        {
            throw new InvalidOperationException("The goal document contains invalid fields.");
        }

        using (JsonDocument.Parse(document.ObjectiveJson, new JsonDocumentOptions { MaxDepth = 128 }))
        using (JsonDocument.Parse(document.ProgressJson, new JsonDocumentOptions { MaxDepth = 128 }))
        {
        }

        if (document.Status == GameGoalStatus.Waiting)
        {
            if (document.Wait is null)
            {
                throw new InvalidOperationException("A waiting goal requires a wait condition.");
            }

            _ = new GameGoalWaitCondition(
                document.Wait.TimelineId,
                document.Wait.NotBeforeTick,
                document.Wait.EventTypes ?? Array.Empty<string>());
            if (document.Wait.NotBeforeTick is null && (document.Wait.EventTypes?.Length ?? 0) == 0)
            {
                throw new InvalidOperationException("A waiting goal requires a tick or event type.");
            }
        }
        else if (document.Wait is not null)
        {
            throw new InvalidOperationException("Only a waiting goal can contain a wait condition.");
        }
        if (IsTerminal(document.Status) != (document.TerminalSequence > 0))
        {
            throw new InvalidOperationException("Terminal goal state and terminal sequence must agree.");
        }
    }

    private static void Write(GameAgentExtensionState state, GoalDocument document) =>
        state.Set(GoalPrefix + document.Id, JsonSerializer.Serialize(document));

    private static ToolResult JsonResult(object value) =>
        new(new AgentContent[] { new JsonContent(JsonSerializer.Serialize(value)) });

    private static GoalDocument ToDocument(GameGoalSnapshot goal) => new()
    {
        Id = goal.Id,
        ObjectiveJson = goal.ObjectiveJson,
        ProgressJson = goal.ProgressJson,
        Status = goal.Status,
        Revision = goal.Revision,
        NonProgressUpdates = goal.NonProgressUpdates,
        TerminalSequence = goal.TerminalSequence,
        LastTimelineId = goal.LastTimelineId,
        LastTick = goal.LastTick,
        Error = goal.Error,
        Wait = goal.Wait is null
            ? null
            : new GoalWaitDocument
            {
                TimelineId = goal.Wait.TimelineId,
                NotBeforeTick = goal.Wait.NotBeforeTick,
                EventTypes = goal.Wait.EventTypes.ToArray(),
            },
    };
}

internal sealed class GoalDocument
{
    public string Id { get; set; } = string.Empty;

    public string ObjectiveJson { get; set; } = "{}";

    public string ProgressJson { get; set; } = "{}";

    public GameGoalStatus Status { get; set; }

    public long Revision { get; set; }

    public int NonProgressUpdates { get; set; }

    public long TerminalSequence { get; set; }

    public string LastTimelineId { get; set; } = string.Empty;

    public long LastTick { get; set; }

    public string? Error { get; set; }

    public GoalWaitDocument? Wait { get; set; }
}

internal sealed class GoalWaitDocument
{
    public string TimelineId { get; set; } = string.Empty;

    public long? NotBeforeTick { get; set; }

    public string[] EventTypes { get; set; } = Array.Empty<string>();
}
