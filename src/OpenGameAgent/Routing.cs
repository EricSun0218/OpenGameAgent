using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent;

public enum GameRouteKind
{
    QuickResponse,
    Agent,
    Workflow,
}

public sealed class GameRouteDecision
{
    public GameRouteDecision(GameRouteKind route, string reason, string? workflow = null)
        : this(route, reason, workflow, classification: null)
    {
    }

    private GameRouteDecision(
        GameRouteKind route,
        string reason,
        string? workflow,
        GameRouteClassification? classification)
    {
        if (!Enum.IsDefined(typeof(GameRouteKind), route))
        {
            throw new ArgumentOutOfRangeException(nameof(route));
        }

        if (route == GameRouteKind.Workflow && string.IsNullOrWhiteSpace(workflow))
        {
            throw new ArgumentException("A workflow route requires a workflow name.", nameof(workflow));
        }

        if (route != GameRouteKind.Workflow && workflow is not null)
        {
            throw new ArgumentException("Only a workflow route can specify a workflow name.", nameof(workflow));
        }

        Route = route;
        Reason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;
        Workflow = workflow;
        Classification = classification;
    }

    public GameRouteKind Route { get; }

    public string Reason { get; }

    public string? Workflow { get; }

    /// <summary>
    /// Model-classifier outcome and any conservative fallback used by the automatic route policy.
    /// Null when no classifier participated in route selection.
    /// </summary>
    public GameRouteClassification? Classification { get; }

    public static GameRouteDecision Quick(string reason) => new(GameRouteKind.QuickResponse, reason);

    public static GameRouteDecision Agent(string reason) => new(GameRouteKind.Agent, reason);

    public static GameRouteDecision ToWorkflow(string workflow, string reason) =>
        new(GameRouteKind.Workflow, reason, workflow);

    internal GameRouteDecision WithClassification(GameRouteClassification classification) =>
        new(Route, Reason, Workflow, classification ?? throw new ArgumentNullException(nameof(classification)));
}

public enum GameRouteClassificationFailure
{
    Provider,
    Timeout,
    Empty,
    InvalidJson,
    InvalidRoute,
    BudgetExhausted,
    NoDecision,
    ReasoningOnly,
}

public sealed class GameRouteClassification
{
    internal GameRouteClassification(
        GameRouteClassificationFailure? failure,
        bool usedFallback = false,
        string? fallbackReason = null,
        IReadOnlyList<AgentContentKind>? responseContentKinds = null,
        long visibleContentCharacters = 0,
        long reasoningCharacters = 0,
        int? providerStatusCode = null,
        string? providerFailureCategory = null)
    {
        if (usedFallback != (fallbackReason is not null))
        {
            throw new ArgumentException("A route-classification fallback requires exactly one fallback reason.", nameof(fallbackReason));
        }

        Failure = failure;
        UsedFallback = usedFallback;
        FallbackReason = fallbackReason;
        if (visibleContentCharacters < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(visibleContentCharacters));
        }

        if (reasoningCharacters < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reasoningCharacters));
        }

        if (providerStatusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(providerStatusCode));
        }

        var kinds = (responseContentKinds ?? Array.Empty<AgentContentKind>())
            .Select(kind => Enum.IsDefined(typeof(AgentContentKind), kind)
                ? kind
                : throw new ArgumentOutOfRangeException(nameof(responseContentKinds)))
            .Distinct()
            .OrderBy(kind => kind)
            .ToArray();
        ResponseContentKinds = Array.AsReadOnly(kinds);
        VisibleContentCharacters = visibleContentCharacters;
        ReasoningCharacters = reasoningCharacters;
        ProviderStatusCode = providerStatusCode;
        ProviderFailureCategory = providerFailureCategory is null
            ? null
            : GameJson.RequireId(providerFailureCategory, nameof(providerFailureCategory));
    }

    public bool Selected => Failure is null && !UsedFallback;

    public GameRouteClassificationFailure? Failure { get; }

    public string? FailureCode => Failure is null ? null : FailureName(Failure.Value);

    public bool UsedFallback { get; }

    public string? FallbackReason { get; }

    /// <summary>
    /// Bounded response-shape diagnostics. These values never contain model text or reasoning.
    /// </summary>
    public IReadOnlyList<AgentContentKind> ResponseContentKinds { get; }

    public long VisibleContentCharacters { get; }

    public long ReasoningCharacters { get; }

    /// <summary>
    /// Safe provider-failure metadata. Response bodies and provider reasoning are never included.
    /// </summary>
    public int? ProviderStatusCode { get; }

    public string? ProviderFailureCategory { get; }

    internal static GameRouteClassification Success(ClassifierResponseShape shape = default) =>
        new(failure: null, responseContentKinds: shape.ContentKinds,
            visibleContentCharacters: shape.VisibleContentCharacters,
            reasoningCharacters: shape.ReasoningCharacters,
            providerStatusCode: shape.ProviderStatusCode,
            providerFailureCategory: shape.ProviderFailureCategory);

    internal static GameRouteClassification Failed(
        GameRouteClassificationFailure failure,
        ClassifierResponseShape shape = default) =>
        new(failure, responseContentKinds: shape.ContentKinds,
            visibleContentCharacters: shape.VisibleContentCharacters,
            reasoningCharacters: shape.ReasoningCharacters,
            providerStatusCode: shape.ProviderStatusCode,
            providerFailureCategory: shape.ProviderFailureCategory);

    internal GameRouteClassification WithFallback(string reason) =>
        new(Failure ?? GameRouteClassificationFailure.NoDecision, usedFallback: true,
            GameJson.RequireId(reason, nameof(reason)), ResponseContentKinds,
            VisibleContentCharacters, ReasoningCharacters,
            ProviderStatusCode, ProviderFailureCategory);

    internal static string FailureName(GameRouteClassificationFailure failure) => failure switch
    {
        GameRouteClassificationFailure.Provider => "provider",
        GameRouteClassificationFailure.Timeout => "timeout",
        GameRouteClassificationFailure.Empty => "empty",
        GameRouteClassificationFailure.ReasoningOnly => "reasoning-only",
        GameRouteClassificationFailure.InvalidJson => "invalid-json",
        GameRouteClassificationFailure.InvalidRoute => "invalid-route",
        GameRouteClassificationFailure.BudgetExhausted => "budget-exhausted",
        _ => "no-decision",
    };
}

internal readonly struct ClassifierResponseShape
{
    public ClassifierResponseShape(
        IReadOnlyList<AgentContentKind> contentKinds,
        long visibleContentCharacters,
        long reasoningCharacters,
        int? providerStatusCode = null,
        string? providerFailureCategory = null)
    {
        ContentKinds = contentKinds ?? throw new ArgumentNullException(nameof(contentKinds));
        VisibleContentCharacters = visibleContentCharacters;
        ReasoningCharacters = reasoningCharacters;
        ProviderStatusCode = providerStatusCode;
        ProviderFailureCategory = providerFailureCategory;
    }

    public IReadOnlyList<AgentContentKind>? ContentKinds { get; }

    public long VisibleContentCharacters { get; }

    public long ReasoningCharacters { get; }

    public int? ProviderStatusCode { get; }

    public string? ProviderFailureCategory { get; }
}

public sealed class GameRouteContext
{
    private const long DefaultRemainingModelTokens = 10_000_000_000;
    private readonly Func<long> _remainingModelTokens;
    private readonly Action<GameRouteModelUsage>? _recordModelUsage;
    private GameRouteClassification? _classification;

    public GameRouteContext(GameInput input, int availableToolCount, bool hasPendingWork = false)
        : this(
            input,
            availableToolCount,
            hasPendingWork,
            () => DefaultRemainingModelTokens,
            recordModelUsage: null)
    {
    }

    internal GameRouteContext(
        GameInput input,
        int availableToolCount,
        bool hasPendingWork,
        Func<long> remainingModelTokens,
        Action<GameRouteModelUsage>? recordModelUsage)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        AvailableToolCount = availableToolCount >= 0
            ? availableToolCount
            : throw new ArgumentOutOfRangeException(nameof(availableToolCount));
        HasPendingWork = hasPendingWork;
        _remainingModelTokens = remainingModelTokens
            ?? throw new ArgumentNullException(nameof(remainingModelTokens));
        _recordModelUsage = recordModelUsage;
    }

    public GameInput Input { get; }

    public int AvailableToolCount { get; }

    public bool HasPendingWork { get; }

    /// <summary>
    /// Remaining model-token budget for routing work and the selected execution route.
    /// Policies that call a model should bound that call to this value.
    /// </summary>
    public long RemainingModelTokens
    {
        get
        {
            var remaining = _remainingModelTokens();
            return remaining is >= 0 and <= DefaultRemainingModelTokens
                ? remaining
                : throw new InvalidOperationException("The route token-budget provider returned an invalid value.");
        }
    }

    /// <summary>
    /// Records a model call made while selecting the route. Runtime-created contexts persist the
    /// usage with the same input ledger; standalone contexts accept the call as a no-op.
    /// </summary>
    public void RecordModelUsage(GameRouteModelUsage usage) =>
        _recordModelUsage?.Invoke(usage ?? throw new ArgumentNullException(nameof(usage)));

    internal GameRouteClassification? Classification => _classification;

    internal void RecordClassification(GameRouteClassification classification) =>
        _classification = classification ?? throw new ArgumentNullException(nameof(classification));
}

public sealed class GameRouteModelUsage
{
    public GameRouteModelUsage(
        ModelUsage usage,
        string? runId = null,
        string? detailsJson = null,
        TimeSpan? duration = null)
    {
        Usage = usage ?? throw new ArgumentNullException(nameof(usage));
        RunId = runId is null ? null : GameJson.RequireId(runId, nameof(runId));
        DetailsJson = detailsJson is null ? null : GameJson.RequireValid(detailsJson, nameof(detailsJson));
        if (duration is { } value && (value < TimeSpan.Zero || value > TimeSpan.FromHours(1)))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        Duration = duration;
    }

    public ModelUsage Usage { get; }

    public string? RunId { get; }

    public string? DetailsJson { get; }

    public TimeSpan? Duration { get; }
}

public interface IGameRoutePolicy
{
    ValueTask<GameRouteDecision> RouteAsync(GameRouteContext context, CancellationToken cancellationToken);
}

public delegate ValueTask<GameRouteDecision?> GameRouteClassifier(
    GameRouteContext context,
    CancellationToken cancellationToken);

public sealed class AutomaticGameRoutePolicy : IGameRoutePolicy
{
    private readonly IReadOnlyDictionary<string, GameRouteDecision> _typedRoutes;
    private readonly GameRouteClassifier? _classifier;

    public AutomaticGameRoutePolicy(
        IReadOnlyDictionary<string, GameRouteDecision>? typedRoutes = null,
        GameRouteClassifier? classifier = null)
    {
        if (typedRoutes?.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null) == true)
        {
            throw new ArgumentException("Typed routes require non-empty input types and non-null decisions.", nameof(typedRoutes));
        }

        _typedRoutes = new ReadOnlyDictionary<string, GameRouteDecision>(
            new Dictionary<string, GameRouteDecision>(
                typedRoutes ?? new Dictionary<string, GameRouteDecision>(),
                StringComparer.Ordinal));
        _classifier = classifier;
    }

    public async ValueTask<GameRouteDecision> RouteAsync(
        GameRouteContext context,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (TryReadExplicitRoute(context.Input, out var explicitRoute))
        {
            return explicitRoute;
        }

        if (_typedRoutes.TryGetValue(context.Input.Type, out var typedRoute))
        {
            return typedRoute;
        }

        // Pending work is authoritative and free. Merely having tools available does not prove
        // that this particular input needs them; an optional classifier must still be allowed to
        // select the side-effect-free quick path for ordinary conversation.
        if (context.HasPendingWork)
        {
            return GameRouteDecision.Agent("pending-work");
        }

        if (_classifier is not null)
        {
            var classified = await _classifier(context, cancellationToken).ConfigureAwait(false);
            if (classified is not null)
            {
                return classified.Classification is null
                    ? classified.WithClassification(context.Classification ?? GameRouteClassification.Success())
                    : classified;
            }

            context.RecordClassification(
                context.Classification ?? GameRouteClassification.Failed(GameRouteClassificationFailure.NoDecision));
        }

        var fallbackReason = context.AvailableToolCount > 0 ? "tools-available" : "no-tools-needed";
        if (_classifier is not null)
        {
            var classification = (context.Classification
                    ?? GameRouteClassification.Failed(GameRouteClassificationFailure.NoDecision))
                .WithFallback(fallbackReason);
            var failure = classification.FailureCode ?? "no-decision";
            return (context.AvailableToolCount > 0
                    ? GameRouteDecision.Agent($"classifier-{failure}-fallback-{fallbackReason}")
                    : GameRouteDecision.Quick($"classifier-{failure}-fallback-{fallbackReason}"))
                .WithClassification(classification);
        }

        if (context.AvailableToolCount > 0)
        {
            return GameRouteDecision.Agent("tools-available");
        }

        return GameRouteDecision.Quick("no-tools-needed");
    }

    private static bool TryReadExplicitRoute(GameInput input, out GameRouteDecision decision)
    {
        if (!input.Metadata.TryGetValue("agent.route", out var route))
        {
            decision = null!;
            return false;
        }

        if (string.Equals(route, "quick", StringComparison.OrdinalIgnoreCase))
        {
            decision = GameRouteDecision.Quick("explicit");
            return true;
        }

        if (string.Equals(route, "agent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(route, "direct", StringComparison.OrdinalIgnoreCase))
        {
            decision = GameRouteDecision.Agent("explicit");
            return true;
        }

        if (string.Equals(route, "plan", StringComparison.OrdinalIgnoreCase))
        {
            decision = GameRouteDecision.Agent("explicit-plan");
            return true;
        }

        if (string.Equals(route, "auto", StringComparison.OrdinalIgnoreCase))
        {
            decision = null!;
            return false;
        }

        const string workflowPrefix = "workflow:";
        if (route.StartsWith(workflowPrefix, StringComparison.OrdinalIgnoreCase)
            && route.Length > workflowPrefix.Length)
        {
            decision = GameRouteDecision.ToWorkflow(route.Substring(workflowPrefix.Length), "explicit");
            return true;
        }

        throw new ArgumentException($"Unsupported explicit route '{route}'.", nameof(input));
    }
}

public sealed class ModelGameRouteClassifierOptions
{
    public int MaxOutputTokens { get; set; } = 128;

    public long MaxTotalTokens { get; set; } = 2_048;

    public int TimeoutMilliseconds { get; set; } = 5_000;

    /// <summary>
    /// Provider-facing reasoning level for the classification request. The safe default asks
    /// providers to disable reasoning. For a model that cannot disable reasoning, set a bounded
    /// provider-supported level; any reasoning remains private and is never parsed as a route decision.
    /// Null delegates reasoning control to the provider adapter.
    /// </summary>
    public string? ReasoningLevel { get; set; } = "off";

    internal ModelGameRouteClassifierOptions CopyAndValidate()
    {
        if (MaxOutputTokens is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxOutputTokens));
        }

        if (MaxTotalTokens is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTotalTokens));
        }

        if (TimeoutMilliseconds is < 1 or > 300_000)
        {
            throw new ArgumentOutOfRangeException(nameof(TimeoutMilliseconds));
        }

        if (ReasoningLevel is { } reasoningLevel
            && (string.IsNullOrWhiteSpace(reasoningLevel) || reasoningLevel.Length > 64))
        {
            throw new ArgumentException("The classifier reasoning level must contain 1 to 64 characters.", nameof(ReasoningLevel));
        }

        return new ModelGameRouteClassifierOptions
        {
            MaxOutputTokens = MaxOutputTokens,
            MaxTotalTokens = MaxTotalTokens,
            TimeoutMilliseconds = TimeoutMilliseconds,
            ReasoningLevel = ReasoningLevel,
        };
    }
}

public sealed class ModelGameRouteClassifier
{
    private readonly IModelProvider _provider;
    private readonly string _model;
    private readonly IReadOnlyCollection<string> _workflows;
    private readonly ModelGameRouteClassifierOptions _options;

    public ModelGameRouteClassifier(
        IModelProvider provider,
        string model,
        IReadOnlyCollection<string>? workflows = null,
        ModelGameRouteClassifierOptions? options = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _model = GameJson.RequireId(model, nameof(model));
        var copiedWorkflows = (workflows ?? Array.Empty<string>())
            .Select(value => GameJson.RequireId(value, nameof(workflows)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _workflows = Array.AsReadOnly(copiedWorkflows);
        _options = (options ?? new ModelGameRouteClassifierOptions()).CopyAndValidate();
    }

    public async ValueTask<GameRouteDecision?> ClassifyAsync(
        GameRouteContext context,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        if (context.RemainingModelTokens <= 0)
        {
            context.RecordClassification(GameRouteClassification.Failed(
                GameRouteClassificationFailure.BudgetExhausted));
            return null;
        }

        var options = new AgentOptions(_provider, _model)
        {
            SystemPrompt =
                "Classify the game input route. Reply with one JSON object only. "
                + "Use quick for a response that needs no game action, agent when tools or multi-step reasoning may be needed, "
                + "or workflow for a named deterministic workflow. Available workflows: "
                + JsonSerializer.Serialize(_workflows),
            Parameters = new ModelParameters
            {
                Temperature = 0,
                MaxOutputTokens = _options.MaxOutputTokens,
                ReasoningLevel = _options.ReasoningLevel,
            },
            Limits = new AgentLimits
            {
                MaxTurns = 1,
                MaxTotalTokens = Math.Min(_options.MaxTotalTokens, context.RemainingModelTokens),
                ModelTimeoutMilliseconds = _options.TimeoutMilliseconds + 1_000,
                MaxTextCharactersPerPart = 16_384,
                MaxJsonCharactersPerPart = 1_000_000,
            },
            Hooks = new AgentHooks
            {
                ShouldStopAfterTurnAsync = (_, _) => new ValueTask<bool>(true),
            },
        };
        var inputJson = JsonSerializer.Serialize(new
        {
            inputType = context.Input.Type,
            payload = GameJson.ParseElement(context.Input.PayloadJson),
            availableToolCount = context.AvailableToolCount,
            hasPendingWork = context.HasPendingWork,
        });
        var agent = new Agent(options);
        var startedAt = Stopwatch.GetTimestamp();
        using var classifierCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        classifierCancellation.CancelAfter(_options.TimeoutMilliseconds);
        var result = await agent.RunAsync(
            AgentMessage.UserJson(inputJson),
            classifierCancellation.Token).ConfigureAwait(false);
        var durationMilliseconds = (Stopwatch.GetTimestamp() - startedAt) * 1_000d / Stopwatch.Frequency;
        cancellationToken.ThrowIfCancellationRequested();
        var assistant = result.NewMessages.LastOrDefault(message => message.Role == AgentRole.Assistant);
        var responseShape = InspectResponseShape(assistant);
        if (!result.Succeeded)
        {
            var failure = classifierCancellation.IsCancellationRequested
                ? GameRouteClassificationFailure.Timeout
                : result.Status == AgentRunStatus.LimitExceeded
                    ? GameRouteClassificationFailure.BudgetExhausted
                    : GameRouteClassificationFailure.Provider;
            context.RecordClassification(GameRouteClassification.Failed(failure, responseShape));
            context.RecordModelUsage(new GameRouteModelUsage(
                result.Usage,
                result.RunId,
                CreateUsageDetails("fallback", failure, assistant, responseShape, durationMilliseconds),
                TimeSpan.FromMilliseconds(durationMilliseconds)));
            return null;
        }

        var content = assistant?.Content.OfType<JsonContent>().FirstOrDefault()?.Json
            ?? assistant?.Content.OfType<TextContent>().FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(content))
        {
            var failure = assistant?.StopReason == ModelStopReason.Length
                ? GameRouteClassificationFailure.BudgetExhausted
                : responseShape.ReasoningCharacters > 0
                    ? GameRouteClassificationFailure.ReasoningOnly
                    : GameRouteClassificationFailure.Empty;
            context.RecordClassification(GameRouteClassification.Failed(failure, responseShape));
            context.RecordModelUsage(new GameRouteModelUsage(
                result.Usage,
                result.RunId,
                CreateUsageDetails("fallback", failure, assistant, responseShape, durationMilliseconds),
                TimeSpan.FromMilliseconds(durationMilliseconds)));
            return null;
        }

        var parse = ParseDecision(content);
        if (parse.Decision is not null)
        {
            var classification = GameRouteClassification.Success(responseShape);
            context.RecordClassification(classification);
            context.RecordModelUsage(new GameRouteModelUsage(
                result.Usage,
                result.RunId,
                CreateUsageDetails("selected", failure: null, assistant, responseShape, durationMilliseconds),
                TimeSpan.FromMilliseconds(durationMilliseconds)));
            return parse.Decision.WithClassification(classification);
        }

        context.RecordClassification(GameRouteClassification.Failed(parse.Failure, responseShape));
        context.RecordModelUsage(new GameRouteModelUsage(
            result.Usage,
            result.RunId,
            CreateUsageDetails("fallback", parse.Failure, assistant, responseShape, durationMilliseconds),
            TimeSpan.FromMilliseconds(durationMilliseconds)));
        return null;
    }

    private (GameRouteDecision? Decision, GameRouteClassificationFailure Failure) ParseDecision(string content)
    {
        if (!TryExtractJson(content, out var json))
        {
            return (null, GameRouteClassificationFailure.InvalidJson);
        }

        JsonElement root;
        try
        {
            var validContent = GameJson.RequireValid(json, nameof(content));
            using var document = JsonDocument.Parse(validContent);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return (null, GameRouteClassificationFailure.InvalidJson);
        }
        catch (ArgumentException)
        {
            return (null, GameRouteClassificationFailure.InvalidJson);
        }

        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Any(property => property.Name is not "route" and not "reason" and not "workflow")
            || !root.TryGetProperty("route", out var routeElement)
            || routeElement.ValueKind != JsonValueKind.String)
        {
            return (null, GameRouteClassificationFailure.InvalidRoute);
        }

        var route = routeElement.GetString();
        var reason = "model-classifier";
        if (root.TryGetProperty("reason", out var reasonElement))
        {
            if (reasonElement.ValueKind != JsonValueKind.String
                || reasonElement.GetString() is not { Length: > 0 and <= 512 } parsedReason
                || string.IsNullOrWhiteSpace(parsedReason))
            {
                return (null, GameRouteClassificationFailure.InvalidRoute);
            }

            reason = parsedReason;
        }

        if (string.Equals(route, "quick", StringComparison.OrdinalIgnoreCase)
            && !root.TryGetProperty("workflow", out _))
        {
            return (GameRouteDecision.Quick(reason), default);
        }

        if (string.Equals(route, "agent", StringComparison.OrdinalIgnoreCase)
            && !root.TryGetProperty("workflow", out _))
        {
            return (GameRouteDecision.Agent(reason), default);
        }

        if (string.Equals(route, "workflow", StringComparison.OrdinalIgnoreCase)
            && root.TryGetProperty("workflow", out var workflowElement)
            && workflowElement.ValueKind == JsonValueKind.String
            && workflowElement.GetString() is { } workflow
            && _workflows.Contains(workflow, StringComparer.Ordinal))
        {
            return (GameRouteDecision.ToWorkflow(workflow, reason), default);
        }

        return (null, GameRouteClassificationFailure.InvalidRoute);
    }

    private static bool TryExtractJson(string content, out string json)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            json = trimmed;
            return true;
        }

        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd < 0)
        {
            json = string.Empty;
            return false;
        }

        var opening = trimmed.Substring(0, firstLineEnd).TrimEnd('\r');
        if (!string.Equals(opening, "```json", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(opening, "```", StringComparison.Ordinal))
        {
            json = string.Empty;
            return false;
        }

        var closing = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (closing <= firstLineEnd
            || trimmed.Substring(closing + 3).Trim().Length != 0
            || trimmed.Substring(firstLineEnd + 1, closing - firstLineEnd - 1).Contains("```", StringComparison.Ordinal))
        {
            json = string.Empty;
            return false;
        }

        json = trimmed.Substring(firstLineEnd + 1, closing - firstLineEnd - 1).Trim();
        return json.Length > 0;
    }

    private string CreateUsageDetails(
        string outcome,
        GameRouteClassificationFailure? failure,
        AgentMessage? assistant,
        ClassifierResponseShape responseShape,
        double durationMilliseconds) =>
        JsonSerializer.Serialize(new
        {
            category = "route-classification",
            outcome,
            failure = failure is null ? null : GameRouteClassification.FailureName(failure.Value),
            provider = assistant?.Provider,
            requestedModel = _model,
            model = assistant?.ResponseModel ?? assistant?.Model,
            responseId = assistant?.ResponseId,
            contentKinds = (responseShape.ContentKinds ?? Array.Empty<AgentContentKind>())
                .Select(ContentKindName)
                .ToArray(),
            visibleContentCharacters = responseShape.VisibleContentCharacters,
            reasoningCharacters = responseShape.ReasoningCharacters,
            providerStatusCode = responseShape.ProviderStatusCode,
            providerFailureCategory = responseShape.ProviderFailureCategory,
            durationMilliseconds,
        });

    private static ClassifierResponseShape InspectResponseShape(AgentMessage? assistant)
    {
        if (assistant is null)
        {
            return new ClassifierResponseShape(Array.Empty<AgentContentKind>(), 0, 0);
        }

        var kinds = assistant.Content.Select(content => content.Kind).Distinct().ToArray();
        var visibleCharacters = assistant.Content.Sum(content => content switch
        {
            TextContent text => (long)text.Text.Length,
            JsonContent json => json.Json.Length,
            _ => 0,
        });
        var reasoningCharacters = assistant.Content
            .OfType<ReasoningContent>()
            .Sum(content => (long)content.Text.Length);
        var providerFailure = InspectProviderFailure(assistant.Diagnostics);
        return new ClassifierResponseShape(
            Array.AsReadOnly(kinds),
            visibleCharacters,
            reasoningCharacters,
            providerFailure.StatusCode,
            providerFailure.Category);
    }

    private static (int? StatusCode, string? Category) InspectProviderFailure(
        IReadOnlyList<ModelDiagnostic> diagnostics)
    {
        for (var index = diagnostics.Count - 1; index >= 0; index--)
        {
            var dataJson = diagnostics[index].DataJson;
            if (dataJson is null)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(dataJson, new JsonDocumentOptions { MaxDepth = 8 });
                var root = document.RootElement;
                var statusCode = root.TryGetProperty("statusCode", out var status)
                                 && status.TryGetInt32(out var parsedStatus)
                                 && parsedStatus is >= 100 and <= 599
                    ? parsedStatus
                    : (int?)null;
                var category = root.TryGetProperty("category", out var categoryElement)
                               && categoryElement.ValueKind == JsonValueKind.String
                               && categoryElement.GetString() is { Length: > 0 and <= 64 } parsedCategory
                               && IsSafeProviderCategory(parsedCategory)
                    ? parsedCategory
                    : null;
                if (statusCode is not null || category is not null)
                {
                    return (statusCode, category);
                }
            }
            catch (JsonException)
            {
                // Diagnostics are optional observations; malformed provider metadata is ignored.
            }
        }

        return (null, null);
    }

    private static bool IsSafeProviderCategory(string value) =>
        value.All(character => character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '-' or '_' or '.');

    private static string ContentKindName(AgentContentKind kind) => kind switch
    {
        AgentContentKind.Text => "text",
        AgentContentKind.Json => "json",
        AgentContentKind.Resource => "resource",
        AgentContentKind.ImageAttachment => "image-attachment",
        AgentContentKind.Binary => "binary",
        AgentContentKind.Reasoning => "reasoning",
        AgentContentKind.ToolCall => "tool-call",
        _ => "unknown",
    };
}
