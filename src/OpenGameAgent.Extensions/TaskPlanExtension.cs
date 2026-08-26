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
public enum GameTaskPlanStatus
{
    Active,
    Completed,
    Failed,
    Cancelled,
    Paused,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GameTaskPlanStepStatus
{
    Pending,
    InProgress,
    Completed,
}

public sealed class GameTaskPlanStepSnapshot
{
    internal GameTaskPlanStepSnapshot(TaskPlanStepDocument document)
    {
        Id = document.Id;
        Text = document.Text;
        Status = document.Status;
    }

    public string Id { get; }

    public string Text { get; }

    public GameTaskPlanStepStatus Status { get; }
}

public sealed class GameTaskPlanSnapshot
{
    internal GameTaskPlanSnapshot(TaskPlanDocument document)
    {
        Id = document.Id;
        Objective = document.Objective;
        Status = document.Status;
        Revision = document.Revision;
        TerminalSequence = document.TerminalSequence;
        LastTimelineId = document.LastTimelineId;
        LastTick = document.LastTick;
        Error = document.Error;
        Steps = new ReadOnlyCollection<GameTaskPlanStepSnapshot>(
            document.Steps.Select(step => new GameTaskPlanStepSnapshot(step)).ToArray());
    }

    public string Id { get; }

    public string Objective { get; }

    public GameTaskPlanStatus Status { get; }

    public long Revision { get; }

    internal long TerminalSequence { get; }

    public string LastTimelineId { get; }

    public long LastTick { get; }

    public string? Error { get; }

    public IReadOnlyList<GameTaskPlanStepSnapshot> Steps { get; }
}

public sealed class GameTaskPlanChanged
{
    public GameTaskPlanChanged(
        GameSessionKey session,
        string inputId,
        GameTaskPlanSnapshot plan,
        string reason)
    {
        Session = new GameSessionKey(session.SessionId, session.ActorId);
        InputId = string.IsNullOrWhiteSpace(inputId) || inputId.Length > 1_024
            ? throw new ArgumentException("An input ID must contain 1 to 1024 characters.", nameof(inputId))
            : inputId;
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Reason = reason ?? string.Empty;
    }

    public GameSessionKey Session { get; }

    public string InputId { get; }

    public GameTaskPlanSnapshot Plan { get; }

    public string Reason { get; }
}

public sealed class GameTaskPlanQueryResult
{
    internal GameTaskPlanQueryResult(
        GameSessionKey session,
        long sessionRevision,
        IEnumerable<GameTaskPlanSnapshot> plans)
    {
        Session = new GameSessionKey(session.SessionId, session.ActorId);
        SessionRevision = sessionRevision >= 0
            ? sessionRevision
            : throw new ArgumentOutOfRangeException(nameof(sessionRevision));
        var copy = (plans ?? throw new ArgumentNullException(nameof(plans))).ToArray();
        if (copy.Any(plan => plan is null))
        {
            throw new ArgumentException("Task-plan query results cannot contain null plans.", nameof(plans));
        }

        Plans = Array.AsReadOnly(copy);
    }

    public GameSessionKey Session { get; }

    public long SessionRevision { get; }

    public IReadOnlyList<GameTaskPlanSnapshot> Plans { get; }
}

public enum GameTaskPlanAdvanceStatus
{
    Advanced,
    SessionNotFound,
    PlanNotFound,
    RevisionConflict,
    PlanNotActive,
    InputNotCommitted,
    AlreadyAdvancedForInput,
    EvidenceRejected,
    SessionConflict,
}

public sealed class GameTaskPlanAdvanceResult
{
    internal GameTaskPlanAdvanceResult(
        GameTaskPlanAdvanceStatus status,
        long sessionRevision,
        GameTaskPlanSnapshot? plan)
    {
        Status = status;
        SessionRevision = sessionRevision >= 0
            ? sessionRevision
            : throw new ArgumentOutOfRangeException(nameof(sessionRevision));
        Plan = plan;
    }

    public GameTaskPlanAdvanceStatus Status { get; }

    public bool Advanced => Status == GameTaskPlanAdvanceStatus.Advanced;

    public long SessionRevision { get; }

    public GameTaskPlanSnapshot? Plan { get; }
}

public sealed class GameTaskPlanEvidenceRequest
{
    public GameTaskPlanEvidenceRequest(
        GameInput input,
        GameTaskPlanSnapshot plan,
        GameTaskPlanStepSnapshot step,
        string kind,
        string reference)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Step = step ?? throw new ArgumentNullException(nameof(step));
        Kind = RequireBounded(kind, 128, nameof(kind));
        Reference = RequireBounded(reference, 2_048, nameof(reference));
    }

    public GameInput Input { get; }

    public GameTaskPlanSnapshot Plan { get; }

    public GameTaskPlanStepSnapshot Step { get; }

    public string Kind { get; }

    public string Reference { get; }

    private static string RequireBounded(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"The value must contain 1 to {maximumLength} characters.",
                parameterName);
        }

        return value;
    }
}

public delegate ValueTask<bool> GameTaskPlanEvidenceValidator(
    GameTaskPlanEvidenceRequest request,
    CancellationToken cancellationToken);

public sealed class TaskPlanOptions
{
    public int MaximumActivePlans { get; set; } = 32;

    public int MaximumRetainedTerminalPlans { get; set; } = 32;

    public int MaximumStepsPerPlan { get; set; } = 32;

    public bool AllowModelAdvancement { get; set; } = true;

    internal TaskPlanOptions CopyAndValidate()
    {
        var copy = (TaskPlanOptions)MemberwiseClone();
        if (copy.MaximumActivePlans < 1 || copy.MaximumActivePlans > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumActivePlans));
        }

        if (copy.MaximumRetainedTerminalPlans < 0 || copy.MaximumRetainedTerminalPlans > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRetainedTerminalPlans));
        }

        if (copy.MaximumStepsPerPlan < 1 || copy.MaximumStepsPerPlan > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumStepsPerPlan));
        }

        return copy;
    }
}

public sealed class TaskPlanExtension : IGameAgentExtension
{
    private const string ExtensionId = "opengameagent.task-plans";
    private const int AbsoluteMaximumStepsPerPlan = 64;
    private const string PlanPrefix = "plan/";
    private const string ManageSchema = """
        {
          "type":"object",
          "required":["action","planId"],
          "properties":{
            "action":{"type":"string","enum":["create","advance","replace_remaining","pause","resume","fail","cancel"]},
            "planId":{"type":"string","minLength":1,"maxLength":128},
            "expectedRevision":{"type":"integer","minimum":1},
            "objective":{"type":"string","minLength":1,"maxLength":4096},
            "steps":{"type":"array","minItems":1,"maxItems":64,"items":{"type":"string","minLength":1,"maxLength":1024}},
            "evidence":{"type":"object","required":["kind","reference"],"properties":{"kind":{"type":"string","minLength":1,"maxLength":128},"reference":{"type":"string","minLength":1,"maxLength":2048}},"additionalProperties":false},
            "reason":{"type":"string","maxLength":4096}
          },
          "additionalProperties":false
        }
        """;
    private static readonly string ManageWithoutAdvanceSchema = ManageSchema.Replace(
        "[\"create\",\"advance\",\"replace_remaining\"",
        "[\"create\",\"replace_remaining\"",
        StringComparison.Ordinal);
    private const string ListSchema = """
        {"type":"object","properties":{"includeTerminal":{"type":"boolean"}},"additionalProperties":false}
        """;

    private readonly GameTaskPlanEvidenceValidator _evidenceValidator;
    private readonly TaskPlanOptions _options;

    public TaskPlanExtension(
        GameTaskPlanEvidenceValidator evidenceValidator,
        TaskPlanOptions? options = null)
    {
        _evidenceValidator = evidenceValidator ?? throw new ArgumentNullException(nameof(evidenceValidator));
        _options = (options ?? new TaskPlanOptions()).CopyAndValidate();
    }

    public static GameAgentExtensionChannel<GameTaskPlanChanged> PlanChanged { get; } =
        new("task-plan.changed");

    public GameAgentExtensionDescriptor Descriptor { get; } = new(
        ExtensionId,
        "1.2.0",
        "Persistent ordered task checklists with host-validated advancement and durable pause/resume.",
        new[] { "task-plan", "checklist", "pending-work", "evidence", "pause-resume" });

    public static async ValueTask<GameTaskPlanQueryResult> ReadAsync(
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
            return new GameTaskPlanQueryResult(key, 0, Array.Empty<GameTaskPlanSnapshot>());
        }

        if (snapshot.Key != key)
        {
            throw new InvalidOperationException("The session store returned a different session key.");
        }

        var plans = ReadAll(
                StoredExtensionStateReader.Read(snapshot, ExtensionId),
                AbsoluteMaximumStepsPerPlan)
            .Where(plan => includeTerminal || !IsTerminal(plan.Status))
            .OrderBy(plan => plan.Id, StringComparer.Ordinal)
            .ToArray();
        return new GameTaskPlanQueryResult(key, snapshot.Revision, plans);
    }

    public async ValueTask<GameTaskPlanAdvanceResult> AdvanceAsync(
        IGameSessionStore sessionStore,
        GameSessionKey session,
        GameInput input,
        string planId,
        long expectedRevision,
        string evidenceKind,
        string evidenceReference,
        CancellationToken cancellationToken = default)
    {
        if (sessionStore is null)
        {
            throw new ArgumentNullException(nameof(sessionStore));
        }

        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (string.IsNullOrWhiteSpace(planId) || planId.Length > 128)
        {
            throw new ArgumentException("A plan ID must contain 1 to 128 characters.", nameof(planId));
        }

        if (expectedRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        var key = new GameSessionKey(session.SessionId, session.ActorId);
        if (!string.Equals(input.SessionId, key.SessionId, StringComparison.Ordinal)
            || !string.Equals(input.ActorId, key.ActorId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The input must belong to the task-plan session.", nameof(input));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await sessionStore.LoadAsync(key, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return new GameTaskPlanAdvanceResult(GameTaskPlanAdvanceStatus.SessionNotFound, 0, null);
        }

        if (snapshot.PendingInputId is not null
            || !snapshot.ProcessedInputIds.Contains(input.InputId, StringComparer.Ordinal))
        {
            return new GameTaskPlanAdvanceResult(
                GameTaskPlanAdvanceStatus.InputNotCommitted,
                snapshot.Revision,
                null);
        }

        var extensionState = new Dictionary<string, string>(
            StoredExtensionStateReader.Read(snapshot, ExtensionId),
            StringComparer.Ordinal);
        if (!extensionState.TryGetValue(PlanPrefix + planId, out var json))
        {
            return new GameTaskPlanAdvanceResult(
                GameTaskPlanAdvanceStatus.PlanNotFound,
                snapshot.Revision,
                null);
        }

        var document = Decode(json, planId, _options.MaximumStepsPerPlan);
        var advance = await AdvanceDocumentAsync(
                input,
                document,
                expectedRevision,
                evidenceKind,
                evidenceReference,
                () => NextTerminalSequence(extensionState),
                cancellationToken)
            .ConfigureAwait(false);
        if (advance != GameTaskPlanAdvanceStatus.Advanced)
        {
            return new GameTaskPlanAdvanceResult(
                advance,
                snapshot.Revision,
                new GameTaskPlanSnapshot(document));
        }

        document.Revision = checked(document.Revision + 1);
        document.LastTimelineId = input.Moment.TimelineId;
        document.LastTick = input.Moment.Tick;
        ValidateDocument(document, planId, _options.MaximumStepsPerPlan);
        extensionState[PlanPrefix + planId] = JsonSerializer.Serialize(document);
        if (IsTerminal(document.Status))
        {
            PruneTerminalPlans(extensionState);
        }

        var storedState = new Dictionary<string, string>(snapshot.ExtensionState, StringComparer.Ordinal);
        var storedPrefix = Uri.EscapeDataString(ExtensionId) + ":";
        foreach (var storedKey in storedState.Keys
                     .Where(candidate => candidate.StartsWith(storedPrefix, StringComparison.Ordinal))
                     .ToArray())
        {
            storedState.Remove(storedKey);
        }

        foreach (var pair in extensionState)
        {
            storedState.Add(
                storedPrefix + Uri.EscapeDataString(pair.Key),
                pair.Value);
        }

        var next = new GameSessionSnapshot(
            snapshot.Key,
            checked(snapshot.Revision + 1),
            snapshot.Messages,
            snapshot.ProcessedInputIds,
            snapshot.LastMoment,
            storedState,
            snapshot.PendingInputId,
            snapshot.UsageLedger);
        var save = await sessionStore.SaveAsync(next, snapshot.Revision, cancellationToken).ConfigureAwait(false);
        if (!save.Saved)
        {
            var currentState = StoredExtensionStateReader.Read(save.Current, ExtensionId);
            var currentPlan = currentState.TryGetValue(PlanPrefix + planId, out var currentJson)
                ? new GameTaskPlanSnapshot(Decode(currentJson, planId, _options.MaximumStepsPerPlan))
                : null;
            return new GameTaskPlanAdvanceResult(
                GameTaskPlanAdvanceStatus.SessionConflict,
                save.Current.Revision,
                currentPlan);
        }

        return new GameTaskPlanAdvanceResult(
            GameTaskPlanAdvanceStatus.Advanced,
            save.Current.Revision,
            new GameTaskPlanSnapshot(document));
    }

    public void Configure(GameAgentExtensionApi api)
    {
        api.RegisterContextProvider(
            "task-plan-guidance",
            (context, _) =>
            {
                if (!AllowsPersistentPlanning(context))
                {
                    return new ValueTask<IReadOnlyList<GameContextSlice>>(Array.Empty<GameContextSlice>());
                }

                PruneTerminalPlans(context.State);
                return new ValueTask<IReadOnlyList<GameContextSlice>>(new[]
                {
                    new GameContextSlice(
                        "task-plan-guidance",
                        JsonSerializer.Serialize(
                            "Use manage_task_plan only for multi-step work that must survive later inputs. An active or paused plan retains exactly one in-progress step. Paused plans do not advance. Advance only with host-verifiable evidence, and replace only unfinished work when new world state invalidates the plan.")),
                });
            });
        api.RegisterToolProvider(
            "task-plan-tools",
            (context, _) => new ValueTask<IReadOnlyList<AgentTool>>(
                !AllowsPersistentPlanning(context)
                    ? Array.Empty<AgentTool>()
                    : new[]
                    {
                        CreateManageTool(api, context),
                        CreateListTool(context),
                    }));
    }

    private static bool AllowsPersistentPlanning(GameAgentExtensionRunContext context) =>
        context.ExecutionScope.Allows(GameExecutionCapabilities.PersistentPlanning);

    private AgentTool CreateManageTool(GameAgentExtensionApi api, GameAgentExtensionRunContext context) =>
        new(
            new ToolDefinition(
                "manage_task_plan",
                _options.AllowModelAdvancement
                    ? "Create, advance, replan, pause, resume, fail, or cancel a persistent ordered checklist for the current actor session. Advancing the final step completes the plan."
                    : "Create, replan, pause, resume, fail, or cancel a persistent ordered checklist for the current actor session. The host owns evidence advancement.",
                _options.AllowModelAdvancement ? ManageSchema : ManageWithoutAdvanceSchema),
            async (arguments, _, cancellationToken) =>
            {
                var action = arguments.GetProperty("action").GetString() ?? string.Empty;
                var planId = arguments.GetProperty("planId").GetString() ?? string.Empty;
                TaskPlanDocument document;

                if (string.Equals(action, "create", StringComparison.Ordinal))
                {
                    if (Read(context.State, planId) is not null)
                    {
                        return ToolResult.Error($"Task plan '{planId}' already exists.", ToolFailureCategory.Conflict);
                    }

                    PruneTerminalPlans(context.State);
                    var activeCount = ReadAll(context.State).Count(plan => !IsTerminal(plan.Status));
                    if (activeCount >= _options.MaximumActivePlans)
                    {
                        return ToolResult.Error(
                            $"At most {_options.MaximumActivePlans} active or paused task plans may exist in one actor session.",
                            ToolFailureCategory.RuleRejected);
                    }

                    if (!arguments.TryGetProperty("objective", out var objectiveElement)
                        || !arguments.TryGetProperty("steps", out var stepsElement))
                    {
                        return ToolResult.Error(
                            "Creating a task plan requires an objective and ordered steps.",
                            ToolFailureCategory.InvalidArguments);
                    }

                    var steps = ReadSteps(stepsElement);
                    if (steps.Count > _options.MaximumStepsPerPlan)
                    {
                        return ToolResult.Error(
                            $"A task plan can contain at most {_options.MaximumStepsPerPlan} steps.",
                            ToolFailureCategory.InvalidArguments);
                    }

                    document = new TaskPlanDocument
                    {
                        Id = planId,
                        Objective = objectiveElement.GetString() ?? string.Empty,
                        Status = GameTaskPlanStatus.Active,
                        Revision = 1,
                        LastTimelineId = context.Input.Moment.TimelineId,
                        LastTick = context.Input.Moment.Tick,
                        Steps = steps.Select((text, index) => new TaskPlanStepDocument
                        {
                            Id = $"step-{index + 1}",
                            Text = text,
                            Status = index == 0
                                ? GameTaskPlanStepStatus.InProgress
                                : GameTaskPlanStepStatus.Pending,
                        }).ToList(),
                    };
                }
                else
                {
                    var existing = Read(context.State, planId);
                    if (existing is null)
                    {
                        return ToolResult.Error($"Task plan '{planId}' does not exist.", ToolFailureCategory.InvalidArguments);
                    }

                    document = existing;
                    if (IsTerminal(document.Status))
                    {
                        return ToolResult.Error($"Task plan '{planId}' is terminal and immutable.", ToolFailureCategory.Conflict);
                    }

                    if (!arguments.TryGetProperty("expectedRevision", out var revisionElement)
                        || revisionElement.GetInt64() != document.Revision)
                    {
                        return ToolResult.Error(
                            $"Task plan '{planId}' revision conflict. Current revision is {document.Revision}.",
                            ToolFailureCategory.Conflict);
                    }

                    if (document.Status == GameTaskPlanStatus.Paused
                        && action is not "pause" and not "resume")
                    {
                        return ToolResult.Error(
                            $"Task plan '{planId}' is paused and must be resumed before it can change.",
                            ToolFailureCategory.Conflict);
                    }

                    var changed = true;
                    switch (action)
                    {
                        case "advance":
                            if (!_options.AllowModelAdvancement)
                            {
                                return ToolResult.Error(
                                    "The host owns evidence advancement for this task-plan extension.",
                                    ToolFailureCategory.RuleRejected);
                            }

                            if (!arguments.TryGetProperty("evidence", out var evidenceElement))
                            {
                                return ToolResult.Error(
                                    "Advancing a task plan requires host-verifiable evidence.",
                                    ToolFailureCategory.InvalidArguments);
                            }

                            var advance = await AdvanceDocumentAsync(
                                    context.Input,
                                    document,
                                    document.Revision,
                                    evidenceElement.GetProperty("kind").GetString() ?? string.Empty,
                                    evidenceElement.GetProperty("reference").GetString() ?? string.Empty,
                                    () => NextTerminalSequence(context.State),
                                    cancellationToken)
                                .ConfigureAwait(false);
                            if (advance == GameTaskPlanAdvanceStatus.AlreadyAdvancedForInput)
                            {
                                return ToolResult.Error(
                                    "A task plan may advance at most once per agent input.",
                                    ToolFailureCategory.Conflict);
                            }

                            if (advance == GameTaskPlanAdvanceStatus.EvidenceRejected)
                            {
                                return ToolResult.Error(
                                    "The host rejected the evidence for advancing this task plan.",
                                    ToolFailureCategory.RuleRejected);
                            }

                            if (advance != GameTaskPlanAdvanceStatus.Advanced)
                            {
                                return ToolResult.Error(
                                    $"The task plan cannot advance ({advance}).",
                                    ToolFailureCategory.Conflict);
                            }

                            break;
                        case "replace_remaining":
                            if (!arguments.TryGetProperty("steps", out var replacementElement))
                            {
                                return ToolResult.Error(
                                    "Replacing unfinished work requires ordered replacement steps.",
                                    ToolFailureCategory.InvalidArguments);
                            }

                            var replacements = ReadSteps(replacementElement);
                            var completed = document.Steps
                                .Where(step => step.Status == GameTaskPlanStepStatus.Completed)
                                .Select(CloneStep)
                                .ToList();
                            if (checked(completed.Count + replacements.Count) > _options.MaximumStepsPerPlan)
                            {
                                return ToolResult.Error(
                                    $"A task plan can contain at most {_options.MaximumStepsPerPlan} steps.",
                                    ToolFailureCategory.InvalidArguments);
                            }

                            var nextRevision = checked(document.Revision + 1);
                            completed.AddRange(replacements.Select((text, index) => new TaskPlanStepDocument
                            {
                                Id = $"step-r{nextRevision}-{index + 1}",
                                Text = text,
                                Status = index == 0
                                    ? GameTaskPlanStepStatus.InProgress
                                    : GameTaskPlanStepStatus.Pending,
                            }));
                            document.Steps = completed;
                            break;
                        case "pause":
                            if (document.Status == GameTaskPlanStatus.Paused)
                            {
                                changed = false;
                            }
                            else
                            {
                                document.Status = GameTaskPlanStatus.Paused;
                            }

                            break;
                        case "resume":
                            if (document.Status == GameTaskPlanStatus.Active)
                            {
                                changed = false;
                            }
                            else
                            {
                                document.Status = GameTaskPlanStatus.Active;
                            }

                            break;
                        case "fail":
                            document.Status = GameTaskPlanStatus.Failed;
                            document.Error = ReadReason(arguments, "The task plan failed.");
                            document.TerminalSequence = NextTerminalSequence(context.State);
                            ClearInProgress(document);
                            break;
                        case "cancel":
                            document.Status = GameTaskPlanStatus.Cancelled;
                            document.Error = ReadReason(arguments, "The task plan was cancelled.");
                            document.TerminalSequence = NextTerminalSequence(context.State);
                            ClearInProgress(document);
                            break;
                        default:
                            return ToolResult.Error(
                                $"Unsupported task-plan action '{action}'.",
                                ToolFailureCategory.InvalidArguments);
                    }

                    if (!changed)
                    {
                        return JsonResult(new GameTaskPlanSnapshot(document));
                    }

                    document.Revision = checked(document.Revision + 1);
                    document.LastTimelineId = context.Input.Moment.TimelineId;
                    document.LastTick = context.Input.Moment.Tick;
                }

                ValidateDocument(document, planId, _options.MaximumStepsPerPlan);
                Write(context.State, document);
                if (IsTerminal(document.Status))
                {
                    PruneTerminalPlans(context.State);
                }

                var snapshot = new GameTaskPlanSnapshot(document);
                await api.PublishAsync(
                    PlanChanged,
                    new GameTaskPlanChanged(
                        new GameSessionKey(context.Input.SessionId, context.Input.ActorId),
                        context.Input.InputId,
                        snapshot,
                        action),
                    cancellationToken).ConfigureAwait(false);
                return JsonResult(snapshot);
            },
            ToolRisk.IdempotentWrite,
            ToolExecutionMode.Sequential,
            conflictKey: arguments => arguments.TryGetProperty("planId", out var planId)
                ? planId.GetString()
                : null);

    private AgentTool CreateListTool(GameAgentExtensionRunContext context) =>
        new(
            new ToolDefinition(
                "list_task_plans",
                "List persistent task plans for the current actor session.",
                ListSchema),
            (arguments, _, _) =>
            {
                var includeTerminal = arguments.TryGetProperty("includeTerminal", out var include)
                    && include.GetBoolean();
                var plans = ReadAll(context.State)
                    .Where(plan => includeTerminal || !IsTerminal(plan.Status))
                    .OrderBy(plan => plan.Id, StringComparer.Ordinal)
                    .ToArray();
                return new ValueTask<ToolResult>(JsonResult(new { plans }));
            },
            ToolRisk.ReadOnly);

    private async ValueTask<GameTaskPlanAdvanceStatus> AdvanceDocumentAsync(
        GameInput input,
        TaskPlanDocument document,
        long expectedRevision,
        string evidenceKind,
        string evidenceReference,
        Func<long> nextTerminalSequence,
        CancellationToken cancellationToken)
    {
        if (document.Revision != expectedRevision)
        {
            return GameTaskPlanAdvanceStatus.RevisionConflict;
        }

        if (document.Status != GameTaskPlanStatus.Active)
        {
            return GameTaskPlanAdvanceStatus.PlanNotActive;
        }

        if (string.Equals(document.LastAdvancedInputId, input.InputId, StringComparison.Ordinal))
        {
            return GameTaskPlanAdvanceStatus.AlreadyAdvancedForInput;
        }

        var current = document.Steps.Single(step => step.Status == GameTaskPlanStepStatus.InProgress);
        var request = new GameTaskPlanEvidenceRequest(
            input,
            new GameTaskPlanSnapshot(document),
            new GameTaskPlanStepSnapshot(current),
            evidenceKind,
            evidenceReference);
        bool accepted;
        try
        {
            accepted = await _evidenceValidator(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            accepted = false;
        }

        if (!accepted)
        {
            return GameTaskPlanAdvanceStatus.EvidenceRejected;
        }

        current.Status = GameTaskPlanStepStatus.Completed;
        var next = document.Steps.FirstOrDefault(step => step.Status == GameTaskPlanStepStatus.Pending);
        if (next is null)
        {
            document.Status = GameTaskPlanStatus.Completed;
            document.TerminalSequence = nextTerminalSequence();
        }
        else
        {
            next.Status = GameTaskPlanStepStatus.InProgress;
        }

        document.LastAdvancedInputId = input.InputId;
        return GameTaskPlanAdvanceStatus.Advanced;
    }

    private static List<string> ReadSteps(JsonElement element) =>
        element.EnumerateArray().Select(step => step.GetString() ?? string.Empty).ToList();

    private static string ReadReason(JsonElement arguments, string fallback) =>
        arguments.TryGetProperty("reason", out var reason) && !string.IsNullOrWhiteSpace(reason.GetString())
            ? reason.GetString()!
            : fallback;

    private static void ClearInProgress(TaskPlanDocument document)
    {
        var current = document.Steps.Single(step => step.Status == GameTaskPlanStepStatus.InProgress);
        current.Status = GameTaskPlanStepStatus.Pending;
    }

    private static TaskPlanStepDocument CloneStep(TaskPlanStepDocument step) => new()
    {
        Id = step.Id,
        Text = step.Text,
        Status = step.Status,
    };

    private static bool IsTerminal(GameTaskPlanStatus status) =>
        status is GameTaskPlanStatus.Completed or GameTaskPlanStatus.Failed or GameTaskPlanStatus.Cancelled;

    private long NextTerminalSequence(GameAgentExtensionState state)
    {
        var maximum = ReadAll(state)
            .Where(plan => IsTerminal(plan.Status))
            .Select(plan => plan.TerminalSequence)
            .DefaultIfEmpty()
            .Max();
        return checked(maximum + 1);
    }

    private long NextTerminalSequence(IReadOnlyDictionary<string, string> state)
    {
        var maximum = ReadAll(state, _options.MaximumStepsPerPlan)
            .Where(plan => IsTerminal(plan.Status))
            .Select(plan => plan.TerminalSequence)
            .DefaultIfEmpty()
            .Max();
        return checked(maximum + 1);
    }

    private void PruneTerminalPlans(GameAgentExtensionState state)
    {
        var expired = ReadAll(state)
            .Where(plan => IsTerminal(plan.Status))
            .OrderByDescending(plan => plan.TerminalSequence)
            .ThenBy(plan => plan.Id, StringComparer.Ordinal)
            .Skip(_options.MaximumRetainedTerminalPlans)
            .ToArray();
        foreach (var plan in expired)
        {
            state.Remove(PlanPrefix + plan.Id);
        }
    }

    private void PruneTerminalPlans(IDictionary<string, string> state)
    {
        var expired = ReadAll(
                new ReadOnlyDictionary<string, string>(
                    new Dictionary<string, string>(state, StringComparer.Ordinal)),
                _options.MaximumStepsPerPlan)
            .Where(plan => IsTerminal(plan.Status))
            .OrderByDescending(plan => plan.TerminalSequence)
            .ThenBy(plan => plan.Id, StringComparer.Ordinal)
            .Skip(_options.MaximumRetainedTerminalPlans)
            .ToArray();
        foreach (var plan in expired)
        {
            state.Remove(PlanPrefix + plan.Id);
        }
    }

    private TaskPlanDocument? Read(GameAgentExtensionState state, string planId)
    {
        var json = state.Get(PlanPrefix + planId);
        return json is null ? null : Decode(json, planId);
    }

    private IReadOnlyList<GameTaskPlanSnapshot> ReadAll(GameAgentExtensionState state)
        => ReadAll(state.Snapshot(), _options.MaximumStepsPerPlan);

    private static IReadOnlyList<GameTaskPlanSnapshot> ReadAll(
        IReadOnlyDictionary<string, string> state,
        int maximumSteps)
    {
        var plans = state
            .Where(pair => pair.Key.StartsWith(PlanPrefix, StringComparison.Ordinal))
            .Select(pair => Decode(pair.Value, pair.Key.Substring(PlanPrefix.Length), maximumSteps))
            .Select(document => new GameTaskPlanSnapshot(document))
            .ToArray();
        var duplicate = plans.GroupBy(plan => plan.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Task-plan state contains duplicate ID '{duplicate.Key}'.");
        }

        return Array.AsReadOnly(plans);
    }

    private TaskPlanDocument Decode(string json, string expectedId)
        => Decode(json, expectedId, _options.MaximumStepsPerPlan);

    private static TaskPlanDocument Decode(string json, string expectedId, int maximumSteps)
    {
        try
        {
            var document = JsonSerializer.Deserialize<TaskPlanDocument>(json)
                ?? throw new InvalidOperationException("The task-plan document is null.");
            ValidateDocument(document, expectedId, maximumSteps);
            return document;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Task plan '{expectedId}' state is invalid.", exception);
        }
    }

    private static void ValidateDocument(TaskPlanDocument document, string expectedId, int maximumSteps)
    {
        if (string.IsNullOrWhiteSpace(document.Id)
            || document.Id.Length > 128
            || !string.Equals(document.Id, expectedId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(document.Objective)
            || document.Objective.Length > 4_096
            || document.Revision < 1
            || document.TerminalSequence < 0
            || string.IsNullOrWhiteSpace(document.LastTimelineId)
            || !Enum.IsDefined(typeof(GameTaskPlanStatus), document.Status)
            || document.Steps is null
            || document.Steps.Count < 1
            || document.Steps.Count > maximumSteps
            || (document.LastAdvancedInputId?.Length ?? 0) > 1_024
            || (document.Error?.Length ?? 0) > 4_096)
        {
            throw new InvalidOperationException("The task-plan document contains invalid fields.");
        }

        var duplicate = document.Steps.GroupBy(step => step.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null
            || document.Steps.Any(step => string.IsNullOrWhiteSpace(step.Id)
                || step.Id.Length > 128
                || string.IsNullOrWhiteSpace(step.Text)
                || step.Text.Length > 1_024
                || !Enum.IsDefined(typeof(GameTaskPlanStepStatus), step.Status)))
        {
            throw new InvalidOperationException("The task-plan steps are invalid.");
        }

        var inProgress = document.Steps.Count(step => step.Status == GameTaskPlanStepStatus.InProgress);
        var resumable = document.Status is GameTaskPlanStatus.Active or GameTaskPlanStatus.Paused;
        if ((resumable && inProgress != 1)
            || (!resumable && inProgress != 0)
            || (document.Status == GameTaskPlanStatus.Completed
                && document.Steps.Any(step => step.Status != GameTaskPlanStepStatus.Completed))
            || IsTerminal(document.Status) != (document.TerminalSequence > 0)
            || ((document.Status is GameTaskPlanStatus.Active
                    or GameTaskPlanStatus.Paused
                    or GameTaskPlanStatus.Completed)
                && document.Error is not null))
        {
            throw new InvalidOperationException("The task-plan status does not match its checklist.");
        }

        var sawInProgress = false;
        var sawPending = false;
        foreach (var step in document.Steps)
        {
            switch (step.Status)
            {
                case GameTaskPlanStepStatus.Completed when !sawInProgress && !sawPending:
                    break;
                case GameTaskPlanStepStatus.InProgress when !sawInProgress && !sawPending:
                    sawInProgress = true;
                    break;
                case GameTaskPlanStepStatus.Pending:
                    sawPending = true;
                    break;
                default:
                    throw new InvalidOperationException("The task-plan checklist is not ordered.");
            }
        }
    }

    private static void Write(GameAgentExtensionState state, TaskPlanDocument document) =>
        state.Set(PlanPrefix + document.Id, JsonSerializer.Serialize(document));

    private static ToolResult JsonResult(object value) =>
        new(new AgentContent[] { new JsonContent(JsonSerializer.Serialize(value)) });
}

internal sealed class TaskPlanDocument
{
    public string Id { get; set; } = string.Empty;

    public string Objective { get; set; } = string.Empty;

    public GameTaskPlanStatus Status { get; set; }

    public long Revision { get; set; }

    public long TerminalSequence { get; set; }

    public string LastTimelineId { get; set; } = string.Empty;

    public long LastTick { get; set; }

    public string? LastAdvancedInputId { get; set; }

    public string? Error { get; set; }

    public List<TaskPlanStepDocument> Steps { get; set; } = new();
}

internal sealed class TaskPlanStepDocument
{
    public string Id { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public GameTaskPlanStepStatus Status { get; set; }
}
