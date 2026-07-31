using System.Collections.ObjectModel;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

public static class NativeWorldSemanticContractIds
{
    public const string WorldV1 = "game-agent.world-definition.v1";

    public const string ClocksV1 = "game-agent.world-clocks.v1";

    public const string NumericsV1 = "game-agent.world-numerics.v1";

    public const string EventsV1 = "game-agent.world-events.v1";

    public const string InteractionsV1 =
        "game-agent.world-interactions.v1";

    public const string AgentsV1 = "game-agent.world-agents.v1";

    public const string KnowledgeV1 = "game-agent.world-knowledge.v1";
}

public static class NativeWorldSemanticReasonCodes
{
    public const string WorldDefinitionMissing =
        "world_semantic_world_definition_missing";

    public const string AmbiguousWorldDefinition =
        "world_semantic_world_definition_ambiguous";

    public const string FileMissing = "world_semantic_file_missing";

    public const string InvalidMediaType =
        "world_semantic_media_type_invalid";

    public const string InvalidContract =
        "world_semantic_contract_invalid";

    public const string InvalidShape = "world_semantic_shape_invalid";

    public const string UnknownField = "world_semantic_field_unknown";

    public const string DuplicateId = "world_semantic_id_duplicate";

    public const string ReferenceMissing =
        "world_semantic_reference_missing";

    public const string InvalidPath = "world_semantic_path_invalid";

    public const string InvalidCondition =
        "world_semantic_condition_invalid";

    public const string InvalidSelector =
        "world_semantic_selector_invalid";

    public const string InvalidEffect =
        "world_semantic_effect_invalid";

    public const string LimitExceeded =
        "world_semantic_limit_exceeded";

    public const string MissingExtension =
        "world_semantic_extension_missing";

    public const string InvalidInitialState =
        "world_semantic_initial_state_invalid";
}

public enum WorldSemanticDiagnosticSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2
}

public sealed class WorldSemanticDiagnostic
{
    public WorldSemanticDiagnostic(
        string code,
        WorldSemanticDiagnosticSeverity severity,
        string path,
        string message)
    {
        Code = WorldValidation.Required(code, nameof(code), 96);
        if (!Enum.IsDefined(
                typeof(WorldSemanticDiagnosticSeverity),
                severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity));
        }

        Severity = severity;
        Path = WorldValidation.Required(path, nameof(path), 1_024);
        Message = WorldValidation.Required(
            message,
            nameof(message),
            2_048);
    }

    public string Code { get; }

    public WorldSemanticDiagnosticSeverity Severity { get; }

    public string Path { get; }

    public string Message { get; }
}

public sealed class NativeWorldPackageCompilation
{
    internal NativeWorldPackageCompilation(
        ActivatedWorldPackage? package,
        IEnumerable<WorldSemanticDiagnostic> diagnostics)
    {
        Package = package;
        Diagnostics =
            new ReadOnlyCollection<WorldSemanticDiagnostic>(
                diagnostics.ToArray());
    }

    public bool Succeeded =>
        Package is not null
        && Diagnostics.All(
            item => item.Severity
                    != WorldSemanticDiagnosticSeverity.Error);

    public ActivatedWorldPackage? Package { get; }

    public IReadOnlyList<WorldSemanticDiagnostic> Diagnostics { get; }
}

public sealed class NativeWorldPackageCompilerOptions
{
    public NativeWorldPackageCompilerOptions(
        int maxClocks = 256,
        int maxNumericSchemas = 1_024,
        int maxEvents = 2_048,
        int maxEffectsPerEvent = 128,
        int maxConditionNodes = 512,
        int maxConditionDepth = 24,
        int maxCatalogEntries = 8_192,
        int maxSelectorCandidates = 4_096)
    {
        MaxClocks = InRange(maxClocks, 1, 1_024, nameof(maxClocks));
        MaxNumericSchemas = InRange(
            maxNumericSchemas,
            1,
            8_192,
            nameof(maxNumericSchemas));
        MaxEvents = InRange(maxEvents, 1, 16_384, nameof(maxEvents));
        MaxEffectsPerEvent = InRange(
            maxEffectsPerEvent,
            1,
            512,
            nameof(maxEffectsPerEvent));
        MaxConditionNodes = InRange(
            maxConditionNodes,
            1,
            8_192,
            nameof(maxConditionNodes));
        MaxConditionDepth = InRange(
            maxConditionDepth,
            1,
            64,
            nameof(maxConditionDepth));
        MaxCatalogEntries = InRange(
            maxCatalogEntries,
            1,
            65_536,
            nameof(maxCatalogEntries));
        MaxSelectorCandidates = InRange(
            maxSelectorCandidates,
            1,
            WorldValidation.MaximumParticipants,
            nameof(maxSelectorCandidates));
    }

    public int MaxClocks { get; }

    public int MaxNumericSchemas { get; }

    public int MaxEvents { get; }

    public int MaxEffectsPerEvent { get; }

    public int MaxConditionNodes { get; }

    public int MaxConditionDepth { get; }

    public int MaxCatalogEntries { get; }

    public int MaxSelectorCandidates { get; }

    private static int InRange(
        int value,
        int minimum,
        int maximum,
        string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}

public sealed class NativeWorldDefinition
{
    private readonly JsonElement _initialState;

    internal NativeWorldDefinition(
        string worldId,
        string defaultTimelineId,
        string entityStateRootPath,
        string relationshipRootPath,
        JsonElement initialState,
        IReadOnlyDictionary<string, long> entityIncarnations,
        string digest)
    {
        WorldId = worldId;
        DefaultTimelineId = defaultTimelineId;
        EntityStateRootPath = entityStateRootPath;
        RelationshipRootPath = relationshipRootPath;
        _initialState = initialState.Clone();
        EntityIncarnations =
            new ReadOnlyDictionary<string, long>(
                new Dictionary<string, long>(
                    entityIncarnations,
                    StringComparer.Ordinal));
        Digest = digest;
    }

    public string WorldId { get; }

    public string DefaultTimelineId { get; }

    public string EntityStateRootPath { get; }

    public string RelationshipRootPath { get; }

    public JsonElement InitialState => _initialState.Clone();

    public IReadOnlyDictionary<string, long> EntityIncarnations { get; }

    public string Digest { get; }
}

public sealed class NativeWorldClockDefinition
{
    public NativeWorldClockDefinition(
        string clockId,
        string statePath,
        long initialTick)
    {
        ClockId = WorldValidation.Required(clockId, nameof(clockId));
        StatePath = WorldJsonPointer.Normalize(
            statePath,
            nameof(statePath));
        InitialTick = initialTick;
    }

    public string ClockId { get; }

    public string StatePath { get; }

    public long InitialTick { get; }
}

public sealed class NativeWorldContentEntry
{
    private readonly JsonElement _data;

    internal NativeWorldContentEntry(
        string entryId,
        string version,
        JsonElement data,
        string digest)
    {
        EntryId = entryId;
        Version = version;
        _data = data.Clone();
        Digest = digest;
    }

    public string EntryId { get; }

    public string Version { get; }

    public JsonElement Data => _data.Clone();

    public string Digest { get; }
}

public sealed class NativeWorldContentCatalog
{
    internal NativeWorldContentCatalog(
        string catalogKind,
        IEnumerable<NativeWorldContentEntry> entries,
        string digest)
    {
        CatalogKind = catalogKind;
        Entries = new ReadOnlyCollection<NativeWorldContentEntry>(
            entries.ToArray());
        Digest = digest;
    }

    public string CatalogKind { get; }

    public IReadOnlyList<NativeWorldContentEntry> Entries { get; }

    public string Digest { get; }
}

public enum NativeWorldComparisonOperator
{
    Exists = 0,
    Missing = 1,
    Equal = 2,
    NotEqual = 3,
    LessThan = 4,
    LessThanOrEqual = 5,
    GreaterThan = 6,
    GreaterThanOrEqual = 7
}

public enum NativeWorldValueSourceKind
{
    World = 0,
    Subject = 1,
    Trigger = 2
}

public sealed class NativeWorldPathReference
{
    public NativeWorldPathReference(
        NativeWorldValueSourceKind source,
        string path)
    {
        if (!Enum.IsDefined(typeof(NativeWorldValueSourceKind), source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        Source = source;
        Path = WorldJsonPointer.Normalize(path, nameof(path));
    }

    public NativeWorldValueSourceKind Source { get; }

    public string Path { get; }
}

public abstract class NativeWorldCondition
{
    internal abstract bool RequiresSubject { get; }
}

public sealed class NativeWorldAlwaysCondition : NativeWorldCondition
{
    internal override bool RequiresSubject => false;
}

public sealed class NativeWorldAllCondition : NativeWorldCondition
{
    public NativeWorldAllCondition(IEnumerable<NativeWorldCondition> children)
    {
        Children = CopyChildren(children);
    }

    public IReadOnlyList<NativeWorldCondition> Children { get; }

    internal override bool RequiresSubject =>
        Children.Any(child => child.RequiresSubject);

    internal static IReadOnlyList<NativeWorldCondition> CopyChildren(
        IEnumerable<NativeWorldCondition> children)
    {
        if (children is null)
        {
            throw new ArgumentNullException(nameof(children));
        }

        var copy = WorldValidation.MaterializeBounded(
                children,
                WorldValidation.MaximumConditionChildren,
                nameof(children))
            .Select(
                item => item
                        ?? throw new ArgumentException(
                            "Condition children cannot contain null.",
                            nameof(children)))
            .ToArray();
        if (copy.Length == 0)
        {
            throw new ArgumentException(
                "A composite condition requires at least one child.",
                nameof(children));
        }

        return new ReadOnlyCollection<NativeWorldCondition>(copy);
    }
}

public sealed class NativeWorldAnyCondition : NativeWorldCondition
{
    public NativeWorldAnyCondition(IEnumerable<NativeWorldCondition> children)
    {
        Children = NativeWorldAllCondition.CopyChildren(children);
    }

    public IReadOnlyList<NativeWorldCondition> Children { get; }

    internal override bool RequiresSubject =>
        Children.Any(child => child.RequiresSubject);
}

public sealed class NativeWorldNotCondition : NativeWorldCondition
{
    public NativeWorldNotCondition(NativeWorldCondition child)
    {
        Child = child ?? throw new ArgumentNullException(nameof(child));
    }

    public NativeWorldCondition Child { get; }

    internal override bool RequiresSubject => Child.RequiresSubject;
}

public sealed class NativeWorldPathCondition : NativeWorldCondition
{
    private readonly JsonElement? _value;

    public NativeWorldPathCondition(
        NativeWorldPathReference path,
        NativeWorldComparisonOperator comparison,
        JsonElement? value = null)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        if (!Enum.IsDefined(
                typeof(NativeWorldComparisonOperator),
                comparison))
        {
            throw new ArgumentOutOfRangeException(nameof(comparison));
        }

        if ((comparison is NativeWorldComparisonOperator.Exists
             or NativeWorldComparisonOperator.Missing)
            == value.HasValue)
        {
            throw new ArgumentException(
                "Existence comparisons forbid a value; other comparisons "
                + "require one.",
                nameof(value));
        }

        if (value.HasValue)
        {
            WorldAuthoritativeJson.Validate(value.Value, nameof(value));
            _value = value.Value.Clone();
        }

        Comparison = comparison;
    }

    public NativeWorldPathReference Path { get; }

    public NativeWorldComparisonOperator Comparison { get; }

    public JsonElement? Value => _value?.Clone();

    internal override bool RequiresSubject =>
        Path.Source == NativeWorldValueSourceKind.Subject;
}

public sealed class NativeWorldTagCondition : NativeWorldCondition
{
    public NativeWorldTagCondition(string tag)
    {
        Tag = WorldValidation.Required(tag, nameof(tag));
    }

    public string Tag { get; }

    internal override bool RequiresSubject => true;
}

public sealed class NativeWorldFixedPointCondition : NativeWorldCondition
{
    public NativeWorldFixedPointCondition(
        NativeWorldPathReference path,
        string numericSchemaId,
        NativeWorldComparisonOperator comparison,
        WorldFixedPointValue value)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        NumericSchemaId = WorldValidation.Required(
            numericSchemaId,
            nameof(numericSchemaId));
        if (comparison is NativeWorldComparisonOperator.Exists
            or NativeWorldComparisonOperator.Missing
            || !Enum.IsDefined(
                typeof(NativeWorldComparisonOperator),
                comparison))
        {
            throw new ArgumentOutOfRangeException(nameof(comparison));
        }

        Comparison = comparison;
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public NativeWorldPathReference Path { get; }

    public string NumericSchemaId { get; }

    public NativeWorldComparisonOperator Comparison { get; }

    public WorldFixedPointValue Value { get; }

    internal override bool RequiresSubject =>
        Path.Source == NativeWorldValueSourceKind.Subject;
}

public sealed class NativeWorldClockCondition : NativeWorldCondition
{
    public NativeWorldClockCondition(
        string clockId,
        NativeWorldComparisonOperator comparison,
        long tick)
    {
        ClockId = WorldValidation.Required(clockId, nameof(clockId));
        if (comparison is NativeWorldComparisonOperator.Exists
            or NativeWorldComparisonOperator.Missing
            || !Enum.IsDefined(
                typeof(NativeWorldComparisonOperator),
                comparison))
        {
            throw new ArgumentOutOfRangeException(nameof(comparison));
        }

        Comparison = comparison;
        Tick = tick;
    }

    public string ClockId { get; }

    public NativeWorldComparisonOperator Comparison { get; }

    public long Tick { get; }

    internal override bool RequiresSubject => false;
}

public abstract class NativeWorldParticipantSelector
{
    internal abstract bool ProducesSubjects { get; }
}

public sealed class NativeWorldSingletonSelector
    : NativeWorldParticipantSelector
{
    internal override bool ProducesSubjects => false;
}

public sealed class NativeWorldEntitySelector
    : NativeWorldParticipantSelector
{
    public NativeWorldEntitySelector(
        string entityId,
        long? requiredIncarnation,
        string role = "subject")
    {
        EntityId = WorldValidation.Required(entityId, nameof(entityId));
        if (requiredIncarnation is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredIncarnation));
        }

        RequiredIncarnation = requiredIncarnation;
        Role = WorldValidation.Required(role, nameof(role), 128);
    }

    public string EntityId { get; }

    public long? RequiredIncarnation { get; }

    public string Role { get; }

    internal override bool ProducesSubjects => true;
}

public sealed class NativeWorldTaggedEntitiesSelector
    : NativeWorldParticipantSelector
{
    public NativeWorldTaggedEntitiesSelector(
        string tag,
        int maximumCandidates,
        string role = "subject")
    {
        Tag = WorldValidation.Required(tag, nameof(tag));
        if (maximumCandidates is < 1
            or > WorldValidation.MaximumParticipants)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCandidates));
        }

        MaximumCandidates = maximumCandidates;
        Role = WorldValidation.Required(role, nameof(role), 128);
    }

    public string Tag { get; }

    public int MaximumCandidates { get; }

    public string Role { get; }

    internal override bool ProducesSubjects => true;
}

public abstract class NativeWorldEntityReference
{
    internal abstract bool RequiresSubject { get; }
}

public sealed class NativeWorldSubjectReference
    : NativeWorldEntityReference
{
    internal override bool RequiresSubject => true;
}

public sealed class NativeWorldLiteralEntityReference
    : NativeWorldEntityReference
{
    public NativeWorldLiteralEntityReference(
        string entityId,
        long incarnation)
    {
        Identity = new GameEntityIdentity(entityId, incarnation);
    }

    public GameEntityIdentity Identity { get; }

    internal override bool RequiresSubject => false;
}

public sealed class NativeWorldInteractionTargetReference
    : NativeWorldEntityReference
{
    public NativeWorldInteractionTargetReference(int targetIndex)
    {
        if (targetIndex is < 0 or > 63)
        {
            throw new ArgumentOutOfRangeException(nameof(targetIndex));
        }

        TargetIndex = targetIndex;
    }

    public int TargetIndex { get; }

    internal override bool RequiresSubject => false;
}

public abstract class NativeWorldEffect
{
    protected NativeWorldEffect(string effectId)
    {
        EffectId = WorldValidation.Required(effectId, nameof(effectId));
    }

    public string EffectId { get; }

    internal abstract bool RequiresSubject { get; }

    internal abstract IEnumerable<string> ReadResourceKeys { get; }

    internal abstract IEnumerable<string> WriteResourceKeys { get; }
}

public sealed class NativeWorldValueEffect : NativeWorldEffect
{
    private readonly JsonElement? _value;

    public NativeWorldValueEffect(
        string effectId,
        NativeWorldEntityReference entity,
        string path,
        string resourceKey,
        WorldValueMutationKind mutationKind,
        JsonElement? value = null)
        : base(effectId)
    {
        Entity = entity ?? throw new ArgumentNullException(nameof(entity));
        Path = WorldJsonPointer.Normalize(path, nameof(path));
        ResourceKey = WorldValidation.Required(
            resourceKey,
            nameof(resourceKey),
            512);
        if (!Enum.IsDefined(typeof(WorldValueMutationKind), mutationKind))
        {
            throw new ArgumentOutOfRangeException(nameof(mutationKind));
        }

        if ((mutationKind == WorldValueMutationKind.Set) != value.HasValue)
        {
            throw new ArgumentException(
                "Set requires a value and remove forbids one.",
                nameof(value));
        }

        if (value.HasValue)
        {
            WorldAuthoritativeJson.Validate(value.Value, nameof(value));
            _value = value.Value.Clone();
        }

        MutationKind = mutationKind;
    }

    public NativeWorldEntityReference Entity { get; }

    public string Path { get; }

    public string ResourceKey { get; }

    public WorldValueMutationKind MutationKind { get; }

    public JsonElement? Value => _value?.Clone();

    internal override bool RequiresSubject => Entity.RequiresSubject;

    internal override IEnumerable<string> ReadResourceKeys =>
        new[] { ResourceKey };

    internal override IEnumerable<string> WriteResourceKeys =>
        new[] { ResourceKey };
}

public sealed class NativeWorldNumericEffect : NativeWorldEffect
{
    public NativeWorldNumericEffect(
        string effectId,
        NativeWorldEntityReference entity,
        string path,
        string resourceKey,
        string numericSchemaId,
        WorldNumericMutationKind mutationKind,
        WorldFixedPointValue operand)
        : base(effectId)
    {
        Entity = entity ?? throw new ArgumentNullException(nameof(entity));
        Path = WorldJsonPointer.Normalize(path, nameof(path));
        ResourceKey = WorldValidation.Required(
            resourceKey,
            nameof(resourceKey),
            512);
        NumericSchemaId = WorldValidation.Required(
            numericSchemaId,
            nameof(numericSchemaId));
        if (!Enum.IsDefined(
                typeof(WorldNumericMutationKind),
                mutationKind))
        {
            throw new ArgumentOutOfRangeException(nameof(mutationKind));
        }

        MutationKind = mutationKind;
        Operand = operand ?? throw new ArgumentNullException(nameof(operand));
    }

    public NativeWorldEntityReference Entity { get; }

    public string Path { get; }

    public string ResourceKey { get; }

    public string NumericSchemaId { get; }

    public WorldNumericMutationKind MutationKind { get; }

    public WorldFixedPointValue Operand { get; }

    internal override bool RequiresSubject => Entity.RequiresSubject;

    internal override IEnumerable<string> ReadResourceKeys =>
        new[] { ResourceKey };

    internal override IEnumerable<string> WriteResourceKeys =>
        new[] { ResourceKey };
}

public sealed class NativeWorldTransferEffect : NativeWorldEffect
{
    public NativeWorldTransferEffect(
        string effectId,
        NativeWorldEntityReference source,
        string sourcePath,
        string sourceResourceKey,
        NativeWorldEntityReference target,
        string targetPath,
        string targetResourceKey,
        string numericSchemaId,
        WorldFixedPointValue amount)
        : base(effectId)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        SourcePath = WorldJsonPointer.Normalize(
            sourcePath,
            nameof(sourcePath));
        TargetPath = WorldJsonPointer.Normalize(
            targetPath,
            nameof(targetPath));
        SourceResourceKey = WorldValidation.Required(
            sourceResourceKey,
            nameof(sourceResourceKey),
            512);
        TargetResourceKey = WorldValidation.Required(
            targetResourceKey,
            nameof(targetResourceKey),
            512);
        NumericSchemaId = WorldValidation.Required(
            numericSchemaId,
            nameof(numericSchemaId));
        Amount = amount ?? throw new ArgumentNullException(nameof(amount));
    }

    public NativeWorldEntityReference Source { get; }

    public string SourcePath { get; }

    public string SourceResourceKey { get; }

    public NativeWorldEntityReference Target { get; }

    public string TargetPath { get; }

    public string TargetResourceKey { get; }

    public string NumericSchemaId { get; }

    public WorldFixedPointValue Amount { get; }

    internal override bool RequiresSubject =>
        Source.RequiresSubject || Target.RequiresSubject;

    internal override IEnumerable<string> ReadResourceKeys =>
        new[] { SourceResourceKey, TargetResourceKey };

    internal override IEnumerable<string> WriteResourceKeys =>
        new[] { SourceResourceKey, TargetResourceKey };
}

public sealed class NativeWorldRelationshipEffect : NativeWorldEffect
{
    private readonly JsonElement? _value;

    public NativeWorldRelationshipEffect(
        string effectId,
        NativeWorldEntityReference source,
        NativeWorldEntityReference target,
        string relationshipTypeId,
        string resourceKey,
        WorldRelationshipMutationKind mutationKind,
        JsonElement? value = null)
        : base(effectId)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        RelationshipTypeId = WorldValidation.Required(
            relationshipTypeId,
            nameof(relationshipTypeId));
        ResourceKey = WorldValidation.Required(
            resourceKey,
            nameof(resourceKey),
            512);
        if (!Enum.IsDefined(
                typeof(WorldRelationshipMutationKind),
                mutationKind))
        {
            throw new ArgumentOutOfRangeException(nameof(mutationKind));
        }

        if ((mutationKind == WorldRelationshipMutationKind.Upsert)
            != value.HasValue)
        {
            throw new ArgumentException(
                "Upsert requires a value and remove forbids one.",
                nameof(value));
        }

        if (value.HasValue)
        {
            WorldAuthoritativeJson.Validate(value.Value, nameof(value));
            _value = value.Value.Clone();
        }

        MutationKind = mutationKind;
    }

    public NativeWorldEntityReference Source { get; }

    public NativeWorldEntityReference Target { get; }

    public string RelationshipTypeId { get; }

    public string ResourceKey { get; }

    public WorldRelationshipMutationKind MutationKind { get; }

    public JsonElement? Value => _value?.Clone();

    internal override bool RequiresSubject =>
        Source.RequiresSubject || Target.RequiresSubject;

    internal override IEnumerable<string> ReadResourceKeys =>
        new[] { ResourceKey };

    internal override IEnumerable<string> WriteResourceKeys =>
        new[] { ResourceKey };
}

public sealed class NativeWorldEmitEventEffect : NativeWorldEffect
{
    private readonly JsonElement? _payload;

    public NativeWorldEmitEventEffect(
        string effectId,
        string eventKind,
        JsonElement? payload = null)
        : base(effectId)
    {
        EventKind = WorldValidation.Required(
            eventKind,
            nameof(eventKind));
        if (payload.HasValue)
        {
            WorldAuthoritativeJson.Validate(
                payload.Value,
                nameof(payload));
            _payload = payload.Value.Clone();
        }
    }

    public string EventKind { get; }

    public JsonElement? Payload => _payload?.Clone();

    internal override bool RequiresSubject => false;

    internal override IEnumerable<string> ReadResourceKeys =>
        Array.Empty<string>();

    internal override IEnumerable<string> WriteResourceKeys =>
        Array.Empty<string>();
}

public abstract class NativeWorldEventTrigger
{
}

public sealed class NativeWorldClockEventTrigger : NativeWorldEventTrigger
{
    public NativeWorldClockEventTrigger(
        string clockId,
        long everyTicks,
        long offsetTicks)
    {
        ClockId = WorldValidation.Required(clockId, nameof(clockId));
        if (everyTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(everyTicks));
        }

        EveryTicks = everyTicks;
        OffsetTicks = offsetTicks;
    }

    public string ClockId { get; }

    public long EveryTicks { get; }

    public long OffsetTicks { get; }
}

public sealed class NativeWorldEmittedEventTrigger
    : NativeWorldEventTrigger
{
    public NativeWorldEmittedEventTrigger(string eventKind)
    {
        EventKind = WorldValidation.Required(
            eventKind,
            nameof(eventKind));
    }

    public string EventKind { get; }
}

public sealed class NativeWorldEventDefinition
{
    internal NativeWorldEventDefinition(
        string definitionId,
        string version,
        int priority,
        NativeWorldEventTrigger trigger,
        NativeWorldParticipantSelector selector,
        NativeWorldCondition condition,
        IEnumerable<NativeWorldEffect> effects,
        IEnumerable<string> readResourceKeys,
        IEnumerable<string> writeResourceKeys,
        string digest)
    {
        DefinitionId = definitionId;
        Version = version;
        Priority = priority;
        Trigger = trigger;
        Selector = selector;
        Condition = condition;
        Effects = new ReadOnlyCollection<NativeWorldEffect>(
            effects.ToArray());
        ReadResourceKeys = WorldValidation.CopyKeys(
            readResourceKeys,
            nameof(readResourceKeys));
        WriteResourceKeys = WorldValidation.CopyKeys(
            writeResourceKeys,
            nameof(writeResourceKeys));
        Digest = digest;
    }

    public string DefinitionId { get; }

    public string Version { get; }

    public int Priority { get; }

    public NativeWorldEventTrigger Trigger { get; }

    public NativeWorldParticipantSelector Selector { get; }

    public NativeWorldCondition Condition { get; }

    public IReadOnlyList<NativeWorldEffect> Effects { get; }

    public IReadOnlyList<string> ReadResourceKeys { get; }

    public IReadOnlyList<string> WriteResourceKeys { get; }

    public string Digest { get; }
}

public sealed class NativeWorldInteractionDefinition
{
    internal NativeWorldInteractionDefinition(
        InteractionDefinition definition,
        NativeWorldCondition availability,
        IEnumerable<NativeWorldEffect> effects,
        string digest)
    {
        Definition = definition ?? throw new ArgumentNullException(
            nameof(definition));
        Availability = availability ?? throw new ArgumentNullException(
            nameof(availability));
        Effects = new ReadOnlyCollection<NativeWorldEffect>(
            effects.ToArray());
        Digest = digest;
    }

    public InteractionDefinition Definition { get; }

    public NativeWorldCondition Availability { get; }

    public IReadOnlyList<NativeWorldEffect> Effects { get; }

    public string Digest { get; }
}

public sealed class ActivatedWorldPackage
{
    private readonly IReadOnlyDictionary<string, NativeWorldClockDefinition>
        _clocksById;

    private readonly IReadOnlyDictionary<string, WorldNumericSchema>
        _numericSchemasById;

    internal ActivatedWorldPackage(
        WorldPackageDefinition sourcePackage,
        NativeWorldDefinition world,
        IEnumerable<NativeWorldClockDefinition> clocks,
        IEnumerable<WorldNumericSchema> numericSchemas,
        IEnumerable<NativeWorldEventDefinition> events,
        IEnumerable<NativeWorldInteractionDefinition> interactions,
        NativeWorldContentCatalog agents,
        NativeWorldContentCatalog knowledge,
        string clocksDigest,
        string numericsDigest,
        string eventsDigest,
        string interactionsDigest,
        string catalogDigest)
    {
        SourcePackage = sourcePackage;
        World = world;
        Clocks = new ReadOnlyCollection<NativeWorldClockDefinition>(
            clocks.ToArray());
        NumericSchemas = new ReadOnlyCollection<WorldNumericSchema>(
            numericSchemas.ToArray());
        Events = new ReadOnlyCollection<NativeWorldEventDefinition>(
            events.ToArray());
        NativeInteractions =
            new ReadOnlyCollection<NativeWorldInteractionDefinition>(
                interactions.ToArray());
        Agents = agents;
        Knowledge = knowledge;
        ClocksDigest = clocksDigest;
        NumericsDigest = numericsDigest;
        EventsDigest = eventsDigest;
        InteractionsDigest = interactionsDigest;
        CatalogDigest = catalogDigest;
        _clocksById = new ReadOnlyDictionary<
            string,
            NativeWorldClockDefinition>(
            Clocks.ToDictionary(
                item => item.ClockId,
                StringComparer.Ordinal));
        _numericSchemasById =
            new ReadOnlyDictionary<string, WorldNumericSchema>(
                NumericSchemas.ToDictionary(
                    item => item.SchemaId,
                    StringComparer.Ordinal));
        InteractionCatalog = new InteractionCatalogSnapshot(
            sourcePackage.PackageId + ".interactions",
            generation: 0,
            NativeInteractions.Select(item => item.Definition),
            catalogDigest);
        var runtime = NativeWorldInteractionRuntime.Build(
            this,
            NativeInteractions);
        EventHandlers = runtime.EventHandlers;
        TransactionalEffects = runtime.TransactionalEffects;
    }

    public WorldPackageDefinition SourcePackage { get; }

    public NativeWorldDefinition World { get; }

    public IReadOnlyList<NativeWorldClockDefinition> Clocks { get; }

    public IReadOnlyList<WorldNumericSchema> NumericSchemas { get; }

    public IReadOnlyList<NativeWorldEventDefinition> Events { get; }

    public IReadOnlyList<NativeWorldInteractionDefinition>
        NativeInteractions
    { get; }

    public InteractionCatalogSnapshot InteractionCatalog { get; }

    public NativeWorldContentCatalog Agents { get; }

    public NativeWorldContentCatalog Knowledge { get; }

    public string ClocksDigest { get; }

    public string NumericsDigest { get; }

    public string EventsDigest { get; }

    public string InteractionsDigest { get; }

    public string CatalogDigest { get; }

    public IWorldEventHandlerRegistry EventHandlers { get; }

    public IWorldTransactionalEventEffectRegistry TransactionalEffects
    { get; }

    public NativeWorldClockDefinition? FindClock(string clockId)
    {
        return _clocksById.TryGetValue(clockId, out var clock)
            ? clock
            : null;
    }

    public WorldNumericSchema? FindNumericSchema(string schemaId)
    {
        return _numericSchemasById.TryGetValue(schemaId, out var schema)
            ? schema
            : null;
    }

    public WorldAuthoritativeStateSnapshot CreateInitialSnapshot(
        string? timelineId = null,
        long timelineEpoch = 0)
    {
        return new WorldAuthoritativeStateSnapshot(
            new WorldAuthoritativeCoordinate(
                World.WorldId,
                timelineId ?? World.DefaultTimelineId,
                timelineEpoch,
                saveRevision: 0,
                stateVersion: 0,
                CatalogDigest),
            World.InitialState,
            World.EntityIncarnations);
    }

    public NativeWorldInteractionAdmissionEvaluator
        CreateInteractionAdmissionEvaluator(
            WorldAuthoritativeStateSnapshot snapshot)
    {
        return new NativeWorldInteractionAdmissionEvaluator(
            this,
            snapshot);
    }
}
