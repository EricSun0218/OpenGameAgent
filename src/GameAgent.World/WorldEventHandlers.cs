using System.Collections.ObjectModel;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

public sealed class WorldEventEvaluationContext
{
    internal WorldEventEvaluationContext(
        WorldEvolutionTrigger trigger,
        WorldEventDefinition definition,
        WorldEventDefinitionHistory history,
        int cascadeDepth,
        string? parentInstanceId,
        object? hostContext)
    {
        Trigger = trigger;
        Definition = definition;
        History = history;
        CascadeDepth = cascadeDepth;
        ParentInstanceId = parentInstanceId;
        HostContext = hostContext;
    }

    public WorldEvolutionTrigger Trigger { get; }

    public WorldEventDefinition Definition { get; }

    public WorldEventDefinitionHistory History { get; }

    public int CascadeDepth { get; }

    public string? ParentInstanceId { get; }

    public object? HostContext { get; }
}

public interface IWorldEventCondition
{
    ValueTask<bool> EvaluateAsync(
        WorldEventEvaluationContext context,
        CancellationToken cancellationToken);
}

public sealed class WorldEventAdmissionDecision
{
    private WorldEventAdmissionDecision(bool accepted, string reasonCode)
    {
        Accepted = accepted;
        ReasonCode = WorldValidation.Required(
            reasonCode,
            nameof(reasonCode),
            96);
    }

    public bool Accepted { get; }

    public string ReasonCode { get; }

    public static WorldEventAdmissionDecision Accept(
        string reasonCode = "accepted")
    {
        return new WorldEventAdmissionDecision(true, reasonCode);
    }

    public static WorldEventAdmissionDecision Reject(string reasonCode)
    {
        return new WorldEventAdmissionDecision(false, reasonCode);
    }
}

/// <summary>
/// Performs a non-mutating host admission check before participants are
/// resolved. Costs and confirmations can be checked here, while their atomic
/// state changes remain part of the eventual effect.
/// </summary>
public interface IWorldEventAdmissionHandler
{
    ValueTask<WorldEventAdmissionDecision> EvaluateAsync(
        WorldEventEvaluationContext context,
        CancellationToken cancellationToken);
}

public interface IWorldEventParticipantSelector
{
    ValueTask<IReadOnlyList<WorldEventParticipant>> SelectAsync(
        WorldEventEvaluationContext context,
        CancellationToken cancellationToken);
}

public interface IWorldEventResolver
{
    ValueTask<IReadOnlyList<WorldEventResolution>> ResolveAsync(
        WorldEventEvaluationContext context,
        IReadOnlyList<WorldEventParticipant> selectedParticipants,
        CancellationToken cancellationToken);
}

public sealed class WorldEventEffectContext
{
    public WorldEventEffectContext(
        WorldEventInstance instance,
        object? hostContext = null)
    {
        Instance = instance
                   ?? throw new ArgumentNullException(nameof(instance));
        HostContext = hostContext;
    }

    public WorldEventInstance Instance { get; }

    public object? HostContext { get; }
}

public sealed class WorldEventEffectResult
{
    public WorldEventEffectResult(
        bool applied,
        string outcomeCode,
        JsonElement? typedResult = null)
    {
        Applied = applied;
        OutcomeCode = WorldValidation.Required(
            outcomeCode,
            nameof(outcomeCode),
            96);
        if (typedResult.HasValue)
        {
            JsonValueInspector.ValidateAndMeasure(
                typedResult.Value,
                new JsonValueLimits(
                    maxUtf8Bytes: 65_536,
                    maxDepth: 24,
                    maxNodes: 4_096,
                    maxStringUtf8Bytes: 16_384,
                    maxContainerItems: 2_048),
                nameof(typedResult));
            TypedResult = typedResult.Value.Clone();
        }
    }

    public bool Applied { get; }

    public string OutcomeCode { get; }

    /// <summary>
    /// Optional bounded structured output for an upper orchestration layer to
    /// feed into a subsequent query or decision.
    /// </summary>
    public JsonElement? TypedResult { get; }
}

public interface IWorldEventEffectHandler
{
    /// <summary>
    /// Applies one already admitted effect. A durable host must coordinate
    /// this mutation and its history append in one authoritative transaction;
    /// calling this method followed by an unrelated append is not an
    /// exactly-once execution protocol.
    /// </summary>
    ValueTask<WorldEventEffectResult> ApplyAsync(
        WorldEventEffectContext context,
        CancellationToken cancellationToken);
}

public interface IWorldEventHandlerRegistry
{
    bool TryGetCondition(
        string handlerId,
        out IWorldEventCondition? handler);

    bool TryGetAdmission(
        string handlerId,
        out IWorldEventAdmissionHandler? handler);

    bool TryGetParticipantSelector(
        string handlerId,
        out IWorldEventParticipantSelector? handler);

    bool TryGetResolver(
        string handlerId,
        out IWorldEventResolver? handler);

    bool TryGetEffect(
        string handlerId,
        out IWorldEventEffectHandler? handler);
}

/// <summary>
/// Builds an immutable handler registry. A frozen registry prevents handler
/// replacement while a plan is being evaluated.
/// </summary>
public sealed class WorldEventHandlerRegistryBuilder
{
    private readonly Dictionary<string, IWorldEventCondition> _conditions =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, IWorldEventParticipantSelector>
        _selectors = new(StringComparer.Ordinal);

    private readonly Dictionary<string, IWorldEventAdmissionHandler>
        _admissions = new(StringComparer.Ordinal);

    private readonly Dictionary<string, IWorldEventResolver> _resolvers =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, IWorldEventEffectHandler> _effects =
        new(StringComparer.Ordinal);

    public WorldEventHandlerRegistryBuilder AddCondition(
        string handlerId,
        IWorldEventCondition handler)
    {
        Add(
            _conditions,
            handlerId,
            handler,
            nameof(handlerId),
            nameof(handler));
        return this;
    }

    public WorldEventHandlerRegistryBuilder AddParticipantSelector(
        string handlerId,
        IWorldEventParticipantSelector handler)
    {
        Add(
            _selectors,
            handlerId,
            handler,
            nameof(handlerId),
            nameof(handler));
        return this;
    }

    public WorldEventHandlerRegistryBuilder AddAdmission(
        string handlerId,
        IWorldEventAdmissionHandler handler)
    {
        Add(
            _admissions,
            handlerId,
            handler,
            nameof(handlerId),
            nameof(handler));
        return this;
    }

    public WorldEventHandlerRegistryBuilder AddResolver(
        string handlerId,
        IWorldEventResolver handler)
    {
        Add(
            _resolvers,
            handlerId,
            handler,
            nameof(handlerId),
            nameof(handler));
        return this;
    }

    public WorldEventHandlerRegistryBuilder AddEffect(
        string handlerId,
        IWorldEventEffectHandler handler)
    {
        Add(
            _effects,
            handlerId,
            handler,
            nameof(handlerId),
            nameof(handler));
        return this;
    }

    public IWorldEventHandlerRegistry Build()
    {
        return new ImmutableWorldEventHandlerRegistry(
            _conditions,
            _admissions,
            _selectors,
            _resolvers,
            _effects);
    }

    private static void Add<THandler>(
        IDictionary<string, THandler> destination,
        string handlerId,
        THandler handler,
        string idParameterName,
        string handlerParameterName)
        where THandler : class
    {
        var normalizedId = WorldValidation.Required(
            handlerId,
            idParameterName);
        if (handler is null)
        {
            throw new ArgumentNullException(handlerParameterName);
        }

        if (!destination.TryAdd(normalizedId, handler))
        {
            throw new ArgumentException(
                "A handler with the same identifier is already registered.",
                idParameterName);
        }
    }
}

internal sealed class ImmutableWorldEventHandlerRegistry
    : IWorldEventHandlerRegistry
{
    private readonly IReadOnlyDictionary<string, IWorldEventCondition>
        _conditions;

    private readonly
        IReadOnlyDictionary<string, IWorldEventParticipantSelector> _selectors;

    private readonly IReadOnlyDictionary<string, IWorldEventAdmissionHandler>
        _admissions;

    private readonly IReadOnlyDictionary<string, IWorldEventResolver>
        _resolvers;

    private readonly IReadOnlyDictionary<string, IWorldEventEffectHandler>
        _effects;

    public ImmutableWorldEventHandlerRegistry(
        IReadOnlyDictionary<string, IWorldEventCondition> conditions,
        IReadOnlyDictionary<string, IWorldEventAdmissionHandler> admissions,
        IReadOnlyDictionary<string, IWorldEventParticipantSelector> selectors,
        IReadOnlyDictionary<string, IWorldEventResolver> resolvers,
        IReadOnlyDictionary<string, IWorldEventEffectHandler> effects)
    {
        _conditions = Copy(conditions);
        _admissions = Copy(admissions);
        _selectors = Copy(selectors);
        _resolvers = Copy(resolvers);
        _effects = Copy(effects);
    }

    public bool TryGetCondition(
        string handlerId,
        out IWorldEventCondition? handler)
    {
        return _conditions.TryGetValue(handlerId, out handler);
    }

    public bool TryGetParticipantSelector(
        string handlerId,
        out IWorldEventParticipantSelector? handler)
    {
        return _selectors.TryGetValue(handlerId, out handler);
    }

    public bool TryGetAdmission(
        string handlerId,
        out IWorldEventAdmissionHandler? handler)
    {
        return _admissions.TryGetValue(handlerId, out handler);
    }

    public bool TryGetResolver(
        string handlerId,
        out IWorldEventResolver? handler)
    {
        return _resolvers.TryGetValue(handlerId, out handler);
    }

    public bool TryGetEffect(
        string handlerId,
        out IWorldEventEffectHandler? handler)
    {
        return _effects.TryGetValue(handlerId, out handler);
    }

    private static IReadOnlyDictionary<string, THandler> Copy<THandler>(
        IReadOnlyDictionary<string, THandler> source)
        where THandler : class
    {
        return new ReadOnlyDictionary<string, THandler>(
            new Dictionary<string, THandler>(
                source,
                StringComparer.Ordinal));
    }
}
