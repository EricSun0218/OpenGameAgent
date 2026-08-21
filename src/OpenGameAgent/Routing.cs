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
    }

    public GameRouteKind Route { get; }

    public string Reason { get; }

    public string? Workflow { get; }

    public static GameRouteDecision Quick(string reason) => new(GameRouteKind.QuickResponse, reason);

    public static GameRouteDecision Agent(string reason) => new(GameRouteKind.Agent, reason);

    public static GameRouteDecision ToWorkflow(string workflow, string reason) =>
        new(GameRouteKind.Workflow, reason, workflow);
}

public sealed class GameRouteContext
{
    private const long DefaultRemainingModelTokens = 10_000_000_000;
    private readonly Func<long> _remainingModelTokens;
    private readonly Action<GameRouteModelUsage>? _recordModelUsage;

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
                return classified;
            }
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

public sealed class ModelGameRouteClassifier
{
    private readonly IModelProvider _provider;
    private readonly string _model;
    private readonly IReadOnlyCollection<string> _workflows;

    public ModelGameRouteClassifier(
        IModelProvider provider,
        string model,
        IReadOnlyCollection<string>? workflows = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _model = GameJson.RequireId(model, nameof(model));
        var copiedWorkflows = (workflows ?? Array.Empty<string>())
            .Select(value => GameJson.RequireId(value, nameof(workflows)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _workflows = Array.AsReadOnly(copiedWorkflows);
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
                MaxOutputTokens = 128,
            },
            Limits = new AgentLimits
            {
                MaxTurns = 1,
                MaxTotalTokens = Math.Min(16_384, context.RemainingModelTokens),
                ModelTimeoutMilliseconds = 15_000,
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
        var result = await agent.RunAsync(AgentMessage.UserJson(inputJson), cancellationToken).ConfigureAwait(false);
        var durationMilliseconds = (Stopwatch.GetTimestamp() - startedAt) * 1_000d / Stopwatch.Frequency;
        if (!result.Succeeded)
        {
            context.RecordModelUsage(new GameRouteModelUsage(
                result.Usage,
                result.RunId,
                CreateUsageDetails("failed", assistant: null, durationMilliseconds),
                TimeSpan.FromMilliseconds(durationMilliseconds)));
            return null;
        }

        var assistant = result.NewMessages.LastOrDefault(message => message.Role == AgentRole.Assistant);
        var content = assistant?.Content.OfType<JsonContent>().FirstOrDefault()?.Json
            ?? assistant?.Content.OfType<TextContent>().FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(content))
        {
            context.RecordModelUsage(new GameRouteModelUsage(
                result.Usage,
                result.RunId,
                CreateUsageDetails("empty", assistant, durationMilliseconds),
                TimeSpan.FromMilliseconds(durationMilliseconds)));
            return null;
        }

        GameRouteDecision? decision = null;
        try
        {
            var validContent = GameJson.RequireValid(content, nameof(content));
            using var document = JsonDocument.Parse(validContent);
            var root = document.RootElement;
            var route = root.GetProperty("route").GetString();
            var reason = root.TryGetProperty("reason", out var reasonElement)
                ? reasonElement.GetString() ?? "model-classifier"
                : "model-classifier";
            if (string.Equals(route, "quick", StringComparison.OrdinalIgnoreCase))
            {
                decision = GameRouteDecision.Quick(reason);
            }
            else if (string.Equals(route, "agent", StringComparison.OrdinalIgnoreCase))
            {
                decision = GameRouteDecision.Agent(reason);
            }
            else if (string.Equals(route, "workflow", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("workflow", out var workflowElement)
                && workflowElement.GetString() is { } workflow
                && _workflows.Contains(workflow, StringComparer.Ordinal))
            {
                decision = GameRouteDecision.ToWorkflow(workflow, reason);
            }
        }
        catch (JsonException)
        {
        }
        catch (KeyNotFoundException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (ArgumentException)
        {
        }

        context.RecordModelUsage(new GameRouteModelUsage(
            result.Usage,
            result.RunId,
            CreateUsageDetails(decision is null ? "invalid" : "selected", assistant, durationMilliseconds),
            TimeSpan.FromMilliseconds(durationMilliseconds)));
        return decision;
    }

    private static string CreateUsageDetails(
        string outcome,
        AgentMessage? assistant,
        double durationMilliseconds) =>
        JsonSerializer.Serialize(new
        {
            category = "route-classification",
            outcome,
            provider = assistant?.Provider,
            model = assistant?.ResponseModel ?? assistant?.Model,
            responseId = assistant?.ResponseId,
            durationMilliseconds,
        });
}
