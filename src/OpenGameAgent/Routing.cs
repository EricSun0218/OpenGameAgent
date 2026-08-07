using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    public GameRouteContext(GameInput input, int availableToolCount, bool hasPendingWork = false)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        AvailableToolCount = availableToolCount >= 0
            ? availableToolCount
            : throw new ArgumentOutOfRangeException(nameof(availableToolCount));
        HasPendingWork = hasPendingWork;
    }

    public GameInput Input { get; }

    public int AvailableToolCount { get; }

    public bool HasPendingWork { get; }
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

        if (_classifier is not null)
        {
            var classified = await _classifier(context, cancellationToken).ConfigureAwait(false);
            if (classified is not null)
            {
                return classified;
            }
        }

        if (context.HasPendingWork || context.AvailableToolCount > 0)
        {
            return GameRouteDecision.Agent("tools-or-pending-work");
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

        if (string.Equals(route, "agent", StringComparison.OrdinalIgnoreCase))
        {
            decision = GameRouteDecision.Agent("explicit");
            return true;
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
                MaxTotalTokens = 16_384,
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
        var result = await agent.RunAsync(AgentMessage.UserJson(inputJson), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        var assistant = result.NewMessages.LastOrDefault(message => message.Role == AgentRole.Assistant);
        var content = assistant?.Content.OfType<JsonContent>().FirstOrDefault()?.Json
            ?? assistant?.Content.OfType<TextContent>().FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

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
                return GameRouteDecision.Quick(reason);
            }

            if (string.Equals(route, "agent", StringComparison.OrdinalIgnoreCase))
            {
                return GameRouteDecision.Agent(reason);
            }

            if (string.Equals(route, "workflow", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("workflow", out var workflowElement)
                && workflowElement.GetString() is { } workflow
                && _workflows.Contains(workflow, StringComparer.Ordinal))
            {
                return GameRouteDecision.ToWorkflow(workflow, reason);
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

        return null;
    }
}
