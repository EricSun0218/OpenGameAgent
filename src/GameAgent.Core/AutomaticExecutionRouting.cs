using System.Globalization;
using System.Text.Json;

namespace GameAgent.Core;

/// <summary>
/// Optional provider and inference defaults selected with an execution path.
/// Explicit controls on the durable run always take precedence.
/// </summary>
public sealed class ExecutionRouteModelProfile
{
    public ModelInferenceOptions? Inference { get; set; }

    public ProviderRoutePreference? RoutePreference { get; set; }

    internal ExecutionRouteModelProfile Snapshot() =>
        new()
        {
            Inference = Inference?.CloneValidated(),
            RoutePreference = RoutePreference?.CloneValidated()
        };
}

/// <summary>
/// Bounded automatic-routing configuration. The defaults are conservative:
/// only short, scalar dialogue takes the direct path; structured, actionable,
/// long, or otherwise ambiguous input retains Agent capabilities.
/// </summary>
public sealed class AutomaticExecutionRoutingOptions
{
    private static readonly string[] DefaultAgentIntentTerms =
    {
        "analyze", "analyse", "attack", "build", "buy", "call",
        "collect", "craft", "create", "delete", "equip", "execute",
        "fight", "find", "gather", "investigate", "modify", "move",
        "perform", "plan", "remember", "schedule", "search", "sell",
        "send", "travel", "update", "use",
        "分析", "安排", "攻击", "帮我做", "采集", "查找", "创建", "调用",
        "调查", "发送", "更新", "购买", "行动", "计划", "记住", "建造",
        "修改", "删除", "使用", "收集", "搜索", "移动", "战斗", "执行",
        "制作", "装备"
    };

    /// <summary>
    /// Text at or below this character count is direct when no Agent intent
    /// term is present.
    /// </summary>
    public int DirectTextMaxCharacters { get; set; } = 160;

    /// <summary>
    /// Text at or above this character count is automatically Agent work.
    /// Values between the two thresholds are ambiguous.
    /// </summary>
    public int AgentTextMinCharacters { get; set; } = 512;

    public IReadOnlyList<string> AgentIntentTerms { get; set; } =
        DefaultAgentIntentTerms.ToArray();

    /// <summary>
    /// Conservative default used when an optional classifier is absent,
    /// fails, times out, or returns a low-confidence result.
    /// </summary>
    public ExecutionPath AmbiguousFallbackPath { get; set; } =
        ExecutionPath.Agent;

    public double MinimumClassifierConfidence { get; set; } = 0.75;

    public ExecutionRouteModelProfile? DirectModelProfile { get; set; }

    public ExecutionRouteModelProfile? AgentModelProfile { get; set; }

    internal AutomaticExecutionRoutingOptions Snapshot()
    {
        if (DirectTextMaxCharacters is < 1 or > 8_192)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DirectTextMaxCharacters));
        }

        if (AgentTextMinCharacters <= DirectTextMaxCharacters
            || AgentTextMinCharacters > 32_768)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AgentTextMinCharacters));
        }

        if (AmbiguousFallbackPath is not ExecutionPath.Direct
            and not ExecutionPath.Agent)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AmbiguousFallbackPath));
        }

        if (!double.IsFinite(MinimumClassifierConfidence)
            || MinimumClassifierConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumClassifierConfidence));
        }

        var terms = RuntimeGuard.CopyStrings(
            AgentIntentTerms
            ?? throw new ArgumentNullException(nameof(AgentIntentTerms)),
            maxItems: 128,
            maxItemUtf8Bytes: 64,
            nameof(AgentIntentTerms),
            sort: false,
            requireUnique: true);

        return new AutomaticExecutionRoutingOptions
        {
            DirectTextMaxCharacters = DirectTextMaxCharacters,
            AgentTextMinCharacters = AgentTextMinCharacters,
            AgentIntentTerms = terms,
            AmbiguousFallbackPath = AmbiguousFallbackPath,
            MinimumClassifierConfidence = MinimumClassifierConfidence,
            DirectModelProfile = DirectModelProfile?.Snapshot(),
            AgentModelProfile = AgentModelProfile?.Snapshot()
        };
    }
}

/// <summary>
/// Immutable input supplied to an optional application classifier only when
/// the built-in bounded rules consider an input ambiguous.
/// </summary>
public sealed class AutomaticExecutionClassificationRequest
{
    internal AutomaticExecutionClassificationRequest(
        ExecutionRouteRequest route,
        string? text,
        bool hasStructuredInput,
        bool hasWorkflowRequest)
    {
        Route = ExecutionRouteValidation.Snapshot(route);
        Text = text;
        HasStructuredInput = hasStructuredInput;
        HasWorkflowRequest = hasWorkflowRequest;
    }

    public ExecutionRouteRequest Route { get; }

    public string? Text { get; }

    public bool HasStructuredInput { get; }

    public bool HasWorkflowRequest { get; }
}

public sealed class AutomaticExecutionClassification
{
    public AutomaticExecutionClassification(
        ExecutionPath path,
        double confidence)
    {
        if (!Enum.IsDefined(typeof(ExecutionPath), path))
        {
            throw new ArgumentOutOfRangeException(nameof(path));
        }

        if (!double.IsFinite(confidence) || confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence));
        }

        Path = path;
        Confidence = confidence;
    }

    public ExecutionPath Path { get; }

    public double Confidence { get; }
}

/// <summary>
/// Optional local-rule or small-model classifier for ambiguous inputs. It is
/// executed inside the router's existing timeout, cancellation, and
/// concurrency boundary.
/// </summary>
public interface IAutomaticExecutionClassifier
{
    string ClassifierId { get; }

    string Version { get; }

    ValueTask<AutomaticExecutionClassification> ClassifyAsync(
        AutomaticExecutionClassificationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// A runtime-owned, immutable summary of the latest game input. Natural
/// language is optional; structured content is represented by the flag rather
/// than copied into an unbounded classifier prompt.
/// </summary>
public sealed class ContextualExecutionRouteRequest
{
    internal ContextualExecutionRouteRequest(
        ExecutionRouteRequest route,
        string? latestText,
        bool hasStructuredInput,
        int inputPartCount,
        bool hasWorkflowRequest)
    {
        Route = ExecutionRouteValidation.Snapshot(route);
        LatestText = latestText;
        HasStructuredInput = hasStructuredInput;
        InputPartCount = inputPartCount;
        HasWorkflowRequest = hasWorkflowRequest;
    }

    public ExecutionRouteRequest Route { get; }

    public string? LatestText { get; }

    public bool HasStructuredInput { get; }

    public int InputPartCount { get; }

    public bool HasWorkflowRequest { get; }
}

/// <summary>
/// Additive policy surface for routers that need the latest bounded input.
/// Existing <see cref="IExecutionRoutePolicy"/> implementations continue to
/// receive only the explicit route request.
/// </summary>
public interface IContextualExecutionRoutePolicy : IExecutionRoutePolicy
{
    ValueTask<ExecutionRouteDecision> SelectAsync(
        ContextualExecutionRouteRequest request,
        CancellationToken cancellationToken);
}

internal interface IContextualExecutionRouteFallbackPolicy
{
    ExecutionPath SelectFallback(ContextualExecutionRouteRequest request);
}

/// <summary>
/// Hybrid automatic router. Declared requirements and explicit paths remain
/// authoritative. Bounded local rules keep obvious dialogue fast, while
/// actionable, structured, long, or ambiguous work retains Agent capability.
/// An optional classifier is consulted only for ambiguous text.
/// </summary>
public sealed class AutomaticExecutionRoutePolicy :
    IContextualExecutionRoutePolicy,
    IContextualExecutionRouteFallbackPolicy
{
    private const string BasePolicyId = "automatic-complexity-router";
    private const string BaseVersion = "1.0.0";

    private readonly AutomaticExecutionRoutingOptions _options;
    private readonly IAutomaticExecutionClassifier? _classifier;

    public AutomaticExecutionRoutePolicy(
        AutomaticExecutionRoutingOptions? options = null,
        IAutomaticExecutionClassifier? classifier = null)
    {
        _options = (options ?? new AutomaticExecutionRoutingOptions())
            .Snapshot();
        _classifier = classifier;
        string? classifierId = null;
        string? classifierVersion = null;
        if (classifier is not null)
        {
            classifierId = RuntimeGuard.RequiredUtf8(
                classifier.ClassifierId,
                128,
                nameof(classifier));
            classifierVersion = RuntimeGuard.RequiredUtf8(
                classifier.Version,
                64,
                nameof(classifier));
        }

        PolicyId = BasePolicyId;
        Version = BuildVersion(classifierId, classifierVersion);
    }

    public string PolicyId { get; }

    public string Version { get; }

    public ValueTask<ExecutionRouteDecision> SelectAsync(
        ExecutionRouteRequest request,
        CancellationToken cancellationToken) =>
        SelectAsync(
            new ContextualExecutionRouteRequest(
                request,
                latestText: null,
                hasStructuredInput: false,
                inputPartCount: 0,
                hasWorkflowRequest: false),
            cancellationToken);

    public async ValueTask<ExecutionRouteDecision> SelectAsync(
        ContextualExecutionRouteRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var route = ExecutionRouteValidation.Snapshot(request.Route);
        if (route.ExplicitPath.HasValue)
        {
            return Decision(
                route.ExplicitPath.Value,
                ExecutionRouteReasonCodes.Explicit);
        }

        var minimum = ExecutionRouteValidation.MinimumPath(
            route.Requirements);
        if (minimum == ExecutionPath.Workflow)
        {
            return Decision(
                ExecutionPath.Workflow,
                ExecutionRouteReasonCodes.WorkflowRequired);
        }

        if (minimum == ExecutionPath.Agent)
        {
            return Decision(
                ExecutionPath.Agent,
                ExecutionRouteReasonCodes.AgentCapabilitiesRequired);
        }

        var signal = ClassifySignal(
            route.Signal,
            request.HasWorkflowRequest);
        var input = ClassifyContextInput(request);
        var classification = MoreCapable(signal, input);
        if (classification == AutomaticRouteClass.None)
        {
            classification = AutomaticRouteClass.Direct;
        }

        if (classification == AutomaticRouteClass.Ambiguous)
        {
            var classifierText = input == AutomaticRouteClass.Ambiguous
                ? request.LatestText
                : ExtractSignalText(route.Signal);
            classification = await ResolveAmbiguousAsync(
                    route,
                    request,
                    classifierText,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return classification switch
        {
            AutomaticRouteClass.Workflow when request.HasWorkflowRequest =>
                Decision(
                    ExecutionPath.Workflow,
                    ExecutionRouteReasonCodes.AutomaticWorkflow),
            AutomaticRouteClass.Agent or AutomaticRouteClass.Workflow =>
                Decision(
                    ExecutionPath.Agent,
                    ExecutionRouteReasonCodes.AutomaticAgent),
            _ => Decision(
                ExecutionPath.Direct,
                ExecutionRouteReasonCodes.AutomaticDirect)
        };
    }

    ExecutionPath IContextualExecutionRouteFallbackPolicy.SelectFallback(
        ContextualExecutionRouteRequest request)
    {
        var route = ExecutionRouteValidation.Snapshot(request.Route);
        if (route.ExplicitPath.HasValue)
        {
            return route.ExplicitPath.Value;
        }

        var minimum = ExecutionRouteValidation.MinimumPath(
            route.Requirements);
        if (minimum != ExecutionPath.Direct)
        {
            return minimum;
        }

        var classification = MoreCapable(
            ClassifySignal(route.Signal, request.HasWorkflowRequest),
            ClassifyContextInput(request));
        if (classification == AutomaticRouteClass.Ambiguous)
        {
            classification = FromPath(_options.AmbiguousFallbackPath);
        }

        return classification switch
        {
            AutomaticRouteClass.Workflow when request.HasWorkflowRequest =>
                ExecutionPath.Workflow,
            AutomaticRouteClass.Agent or AutomaticRouteClass.Workflow =>
                ExecutionPath.Agent,
            _ => ExecutionPath.Direct
        };
    }

    private async ValueTask<AutomaticRouteClass> ResolveAmbiguousAsync(
        ExecutionRouteRequest route,
        ContextualExecutionRouteRequest context,
        string? classifierText,
        CancellationToken cancellationToken)
    {
        if (_classifier is null)
        {
            return FromPath(_options.AmbiguousFallbackPath);
        }

        try
        {
            var result = await _classifier.ClassifyAsync(
                    new AutomaticExecutionClassificationRequest(
                        route,
                        classifierText,
                        context.HasStructuredInput,
                        context.HasWorkflowRequest),
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is null
                || result.Confidence
                < _options.MinimumClassifierConfidence
                || result.Path == ExecutionPath.Workflow
                && !context.HasWorkflowRequest
                || !ExecutionRouteValidation.CanSatisfy(
                    result.Path,
                    route.Requirements))
            {
                return FromPath(_options.AmbiguousFallbackPath);
            }

            return FromPath(result.Path);
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return FromPath(_options.AmbiguousFallbackPath);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            return FromPath(_options.AmbiguousFallbackPath);
        }
    }

    private AutomaticRouteClass ClassifySignal(
        JsonElement? signal,
        bool hasWorkflowRequest)
    {
        if (!signal.HasValue
            || signal.Value.ValueKind is JsonValueKind.Null
                or JsonValueKind.Undefined)
        {
            return AutomaticRouteClass.None;
        }

        return ClassifyJson(signal.Value, hasWorkflowRequest);
    }

    private AutomaticRouteClass ClassifyJson(
        JsonElement value,
        bool hasWorkflowRequest)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return ClassifyText(value.GetString());
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return AutomaticRouteClass.Direct;
            case JsonValueKind.Array:
                return AutomaticRouteClass.Agent;
            case JsonValueKind.Object:
                {
                    var hinted = ClassifyHints(value, hasWorkflowRequest);
                    var content = value.TryGetProperty("input", out var input)
                        ? ClassifyJson(input, hasWorkflowRequest)
                        : AutomaticRouteClass.Agent;
                    return MoreCapable(hinted, content);
                }
            default:
                return AutomaticRouteClass.Agent;
        }
    }

    private static AutomaticRouteClass ClassifyHints(
        JsonElement value,
        bool hasWorkflowRequest)
    {
        var result = AutomaticRouteClass.None;
        if (TryReadString(value, "complexity", out var complexity))
        {
            result = complexity switch
            {
                "simple" or "direct" => AutomaticRouteClass.Direct,
                "standard" => AutomaticRouteClass.Ambiguous,
                "complex" or "agent" => AutomaticRouteClass.Agent,
                "workflow" or "parallel" when hasWorkflowRequest =>
                    AutomaticRouteClass.Workflow,
                "workflow" or "parallel" => AutomaticRouteClass.Agent,
                _ => AutomaticRouteClass.None
            };
        }

        if (AnyTrue(
                value,
                "requiresTools",
                "requires_tools",
                "requiresSkills",
                "requires_skills",
                "requiresAction",
                "requires_action",
                "multipleModelTurns",
                "multiple_model_turns"))
        {
            result = MoreCapable(result, AutomaticRouteClass.Agent);
        }

        if (AnyTrue(
                value,
                "parallelActors",
                "parallel_actors",
                "requiresWorkflow",
                "requires_workflow"))
        {
            result = MoreCapable(
                result,
                hasWorkflowRequest
                    ? AutomaticRouteClass.Workflow
                    : AutomaticRouteClass.Agent);
        }

        return result;
    }

    private AutomaticRouteClass ClassifyContextInput(
        ContextualExecutionRouteRequest request)
    {
        if (request.HasStructuredInput || request.InputPartCount > 1)
        {
            return AutomaticRouteClass.Agent;
        }

        return ClassifyText(request.LatestText);
    }

    private AutomaticRouteClass ClassifyText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return AutomaticRouteClass.None;
        }

        if (value.Length >= _options.AgentTextMinCharacters)
        {
            return AutomaticRouteClass.Agent;
        }

        if (ContainsAgentIntent(value))
        {
            return AutomaticRouteClass.Agent;
        }

        if (value.Length <= _options.DirectTextMaxCharacters)
        {
            return AutomaticRouteClass.Direct;
        }

        return AutomaticRouteClass.Ambiguous;
    }

    private bool ContainsAgentIntent(string value)
    {
        foreach (var term in _options.AgentIntentTerms)
        {
            var start = 0;
            while (start < value.Length)
            {
                var index = value.IndexOf(
                    term,
                    start,
                    StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    break;
                }

                var leftBoundary = index == 0
                    || !IsAsciiWordCharacter(value[index - 1])
                    || !IsAsciiWordCharacter(term[0]);
                var end = index + term.Length;
                var rightBoundary = end == value.Length
                    || !IsAsciiWordCharacter(value[end])
                    || !IsAsciiWordCharacter(term[^1]);
                if (leftBoundary && rightBoundary)
                {
                    return true;
                }

                start = index + 1;
            }
        }

        return false;
    }

    private static bool IsAsciiWordCharacter(char value) =>
        value is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_';

    private static string? ExtractSignalText(JsonElement? signal)
    {
        if (!signal.HasValue)
        {
            return null;
        }

        var value = signal.Value;
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return value.ValueKind == JsonValueKind.Object
               && value.TryGetProperty("input", out var input)
               && input.ValueKind == JsonValueKind.String
            ? input.GetString()
            : null;
    }

    private ExecutionRouteDecision Decision(
        ExecutionPath path,
        string reasonCode) =>
        new(
            path,
            reasonCode,
            PolicyId,
            Version,
            path switch
            {
                ExecutionPath.Direct => _options.DirectModelProfile,
                ExecutionPath.Agent => _options.AgentModelProfile,
                _ => null
            });

    private string BuildVersion(
        string? classifierId,
        string? classifierVersion)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "automatic-execution-routing.v1");
        digest.Add("directTextMaxCharacters", _options.DirectTextMaxCharacters);
        digest.Add("agentTextMinCharacters", _options.AgentTextMinCharacters);
        digest.Add("ambiguousFallbackPath", _options.AmbiguousFallbackPath.ToString());
        digest.Add(
            "minimumClassifierConfidence",
            _options.MinimumClassifierConfidence.ToString(
                "R",
                CultureInfo.InvariantCulture));
        digest.Add("agentIntentTerms", _options.AgentIntentTerms);
        digest.Add("classifierId", classifierId);
        digest.Add("classifierVersion", classifierVersion);
        AddProfileDigest(digest, "direct", _options.DirectModelProfile);
        AddProfileDigest(digest, "agent", _options.AgentModelProfile);
        return BaseVersion + "+" + digest.Finish().Substring(0, 16);
    }

    private static void AddProfileDigest(
        CanonicalDigestBuilder digest,
        string name,
        ExecutionRouteModelProfile? profile)
    {
        var inference = profile?.Inference;
        var route = profile?.RoutePreference;
        digest.Add(name + ".present", profile is null ? "false" : "true");
        digest.Add(
            name + ".providerIds",
            route?.ProviderIds ?? Array.Empty<string>());
        digest.Add(
            name + ".allowUnlistedFallback",
            route?.AllowUnlistedFallback == true ? "true" : "false");
        digest.Add(
            name + ".reasoningEnabled",
            inference?.ReasoningEnabled?.ToString());
        digest.Add(name + ".reasoningEffort", inference?.ReasoningEffort);
        digest.Add(
            name + ".reasoningTokenBudget",
            inference?.ReasoningTokenBudget?.ToString(
                CultureInfo.InvariantCulture));
        digest.Add(
            name + ".temperature",
            inference?.Temperature?.ToString("R", CultureInfo.InvariantCulture));
        digest.Add(
            name + ".topP",
            inference?.TopP?.ToString("R", CultureInfo.InvariantCulture));
        digest.Add(
            name + ".seed",
            inference?.Seed?.ToString(CultureInfo.InvariantCulture));
        digest.Add(
            name + ".promptCachingEnabled",
            inference?.PromptCachingEnabled?.ToString());
        digest.Add(name + ".promptCacheKey", inference?.PromptCacheKey);
        digest.Add(
            name + ".promptCacheRetention",
            inference?.PromptCacheRetention);
    }

    private static bool TryReadString(
        JsonElement value,
        string name,
        out string result)
    {
        if (value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            result = property.GetString()?.Trim().ToLowerInvariant()
                     ?? string.Empty;
            return true;
        }

        result = string.Empty;
        return false;
    }

    private static bool AnyTrue(
        JsonElement value,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (value.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.True)
            {
                return true;
            }
        }

        return false;
    }

    private static AutomaticRouteClass FromPath(ExecutionPath path) =>
        path switch
        {
            ExecutionPath.Direct => AutomaticRouteClass.Direct,
            ExecutionPath.Agent => AutomaticRouteClass.Agent,
            ExecutionPath.Workflow => AutomaticRouteClass.Workflow,
            _ => AutomaticRouteClass.Agent
        };

    private static AutomaticRouteClass MoreCapable(
        AutomaticRouteClass left,
        AutomaticRouteClass right) =>
        (AutomaticRouteClass)Math.Max((int)left, (int)right);

    private enum AutomaticRouteClass
    {
        None = 0,
        Direct = 1,
        Ambiguous = 2,
        Agent = 3,
        Workflow = 4
    }
}
