using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameAgent.Core;

namespace GameAgent.World;

/// <summary>
/// Compiles inert files from one admitted native package into an immutable,
/// engine-neutral world snapshot. Compilation never installs code, tools, or
/// provider access from package data.
/// </summary>
public sealed class NativeWorldPackageCompiler
{
    private static readonly string[] WorldDefinitionPaths =
    {
        "world.json",
        "content/world.json"
    };

    private readonly NativeWorldPackageCompilerOptions _options;

    private readonly WorldPackageLimits _limits;

    public NativeWorldPackageCompiler(
        NativeWorldPackageCompilerOptions? options = null,
        WorldPackageLimits? limits = null)
    {
        _options = options ?? new NativeWorldPackageCompilerOptions();
        _limits = limits ?? new WorldPackageLimits();
    }

    public NativeWorldPackageCompilation Compile(
        WorldPackageDefinition package,
        IWorldExtensionCapabilityResolver? capabilities = null)
    {
        if (package is null)
        {
            throw new ArgumentNullException(nameof(package));
        }

        var diagnostics = new List<WorldSemanticDiagnostic>();
        ValidateExtensions(package, capabilities, diagnostics);
        try
        {
            var context = new CompilationContext(
                package,
                _options,
                _limits);
            var activated = context.Compile();
            return diagnostics.Any(
                item => item.Severity
                        == WorldSemanticDiagnosticSeverity.Error)
                ? new NativeWorldPackageCompilation(null, diagnostics)
                : new NativeWorldPackageCompilation(
                    activated,
                    diagnostics);
        }
        catch (SemanticCompilationException exception)
        {
            diagnostics.Add(
                new WorldSemanticDiagnostic(
                    exception.Code,
                    WorldSemanticDiagnosticSeverity.Error,
                    exception.Path,
                    exception.Message));
        }
        catch (WorldDataContractException exception)
        {
            diagnostics.Add(
                new WorldSemanticDiagnostic(
                    exception.ReasonCode,
                    WorldSemanticDiagnosticSeverity.Error,
                    "$package",
                    exception.Message));
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidOperationException
            or JsonException
            or OverflowException)
        {
            diagnostics.Add(
                new WorldSemanticDiagnostic(
                    NativeWorldSemanticReasonCodes.InvalidShape,
                    WorldSemanticDiagnosticSeverity.Error,
                    "$package",
                    "Native world semantics are invalid: "
                    + exception.Message));
        }

        return new NativeWorldPackageCompilation(null, diagnostics);
    }

    private static void ValidateExtensions(
        WorldPackageDefinition package,
        IWorldExtensionCapabilityResolver? capabilities,
        ICollection<WorldSemanticDiagnostic> diagnostics)
    {
        foreach (var requirement in package.RequiredExtensions)
        {
            if (capabilities is not null
                && capabilities.IsApproved(
                    requirement.CapabilityId,
                    requirement.VersionRange))
            {
                continue;
            }

            diagnostics.Add(
                new WorldSemanticDiagnostic(
                    NativeWorldSemanticReasonCodes.MissingExtension,
                    WorldSemanticDiagnosticSeverity.Error,
                    "$package/requiredExtensions/"
                    + requirement.CapabilityId,
                    "A required trusted extension is unavailable or "
                    + "unapproved."));
        }
    }

    private sealed class CompilationContext
    {
        private static readonly IReadOnlyDictionary<string, CatalogSpec>
            CatalogSpecs =
                new ReadOnlyDictionary<string, CatalogSpec>(
                    new Dictionary<string, CatalogSpec>(
                        StringComparer.Ordinal)
                    {
                        ["clocks"] = new(
                            "clocks.json",
                            NativeWorldSemanticContractIds.ClocksV1,
                            "clocks"),
                        ["numerics"] = new(
                            "numerics.json",
                            NativeWorldSemanticContractIds.NumericsV1,
                            "schemas"),
                        ["events"] = new(
                            "events.json",
                            NativeWorldSemanticContractIds.EventsV1,
                            "events"),
                        ["interactions"] = new(
                            "interactions.json",
                            NativeWorldSemanticContractIds.InteractionsV1,
                            "interactions"),
                        ["agents"] = new(
                            "agents.json",
                            NativeWorldSemanticContractIds.AgentsV1,
                            "agents"),
                        ["knowledge"] = new(
                            "knowledge.json",
                            NativeWorldSemanticContractIds.KnowledgeV1,
                            "knowledge")
                    });

        private readonly WorldPackageDefinition _package;

        private readonly NativeWorldPackageCompilerOptions _options;

        private readonly WorldPackageLimits _limits;

        private readonly IReadOnlyDictionary<string, WorldPackageFile>
            _files;

        public CompilationContext(
            WorldPackageDefinition package,
            NativeWorldPackageCompilerOptions options,
            WorldPackageLimits limits)
        {
            _package = package;
            _options = options;
            _limits = limits;
            _files = new ReadOnlyDictionary<string, WorldPackageFile>(
                package.Files.ToDictionary(
                    item => item.Path,
                    StringComparer.Ordinal));
        }

        public ActivatedWorldPackage Compile()
        {
            var worldPath = FindWorldDefinitionPath();
            var worldRoot = ReadJson(worldPath);
            RequireObject(
                worldRoot,
                worldPath,
                "contract",
                "worldId",
                "defaultTimelineId",
                "entityStateRootPath",
                "relationshipRootPath",
                "initialState",
                "entityIncarnations",
                "catalogs",
                "extensions");
            RequireContract(
                worldRoot,
                NativeWorldSemanticContractIds.WorldV1,
                worldPath);
            ValidateExtensionsObject(worldRoot, worldPath);

            var worldId = RequiredString(
                worldRoot,
                "worldId",
                worldPath,
                192);
            var defaultTimelineId = RequiredString(
                worldRoot,
                "defaultTimelineId",
                worldPath,
                192);
            var entityRoot = OptionalString(
                                 worldRoot,
                                 "entityStateRootPath",
                                 worldPath,
                                 1_024)
                             ?? "/entities";
            var relationshipRoot = OptionalString(
                                       worldRoot,
                                       "relationshipRootPath",
                                       worldPath,
                                       1_024)
                                   ?? "/relationships";
            entityRoot = NormalizePointer(entityRoot, worldPath);
            relationshipRoot = NormalizePointer(
                relationshipRoot,
                worldPath);
            var initialState = RequiredProperty(
                worldRoot,
                "initialState",
                JsonValueKind.Object,
                worldPath).Clone();
            try
            {
                WorldAuthoritativeStateSnapshot.ValidateState(
                    initialState,
                    nameof(initialState));
            }
            catch (ArgumentException)
            {
                throw Error(
                    NativeWorldSemanticReasonCodes.InvalidInitialState,
                    Path(worldPath, "initialState"),
                    "Initial state must be bounded authoritative JSON "
                    + "without JSON numbers.");
            }

            var incarnations = ReadIncarnations(worldRoot, worldPath);
            var catalogPaths = ResolveCatalogPaths(worldRoot, worldPath);
            var clocks = ReadClocks(catalogPaths["clocks"]);
            var numericSchemas = ReadNumericSchemas(
                catalogPaths["numerics"]);
            initialState = SeedClockState(
                initialState,
                clocks,
                worldPath);

            var clockMap = clocks.ToDictionary(
                item => item.ClockId,
                StringComparer.Ordinal);
            var numericMap = numericSchemas.ToDictionary(
                item => item.SchemaId,
                StringComparer.Ordinal);
            var events = ReadEvents(
                catalogPaths["events"],
                clockMap,
                numericMap);
            ValidateTickResourceBudget(events, clocks);
            var interactions = ReadInteractions(
                catalogPaths["interactions"],
                clockMap,
                numericMap);
            var agents = ReadContentCatalog(
                "agents",
                catalogPaths["agents"]);
            var knowledge = ReadContentCatalog(
                "knowledge",
                catalogPaths["knowledge"]);

            var clocksDigest = DigestClocks(clocks);
            var numericsDigest = DigestNumerics(numericSchemas);
            var eventsDigest = DigestEvents(events);
            var interactionsDigest = DigestInteractions(interactions);
            var worldDigest = DigestWorld(
                worldId,
                defaultTimelineId,
                entityRoot,
                relationshipRoot,
                initialState,
                incarnations);
            var world = new NativeWorldDefinition(
                worldId,
                defaultTimelineId,
                entityRoot,
                relationshipRoot,
                initialState,
                incarnations,
                worldDigest);
            var catalogDigest = DigestCatalogSnapshot(
                _package.PackageDigest,
                worldDigest,
                clocksDigest,
                numericsDigest,
                eventsDigest,
                interactionsDigest,
                agents.Digest,
                knowledge.Digest);
            return new ActivatedWorldPackage(
                _package,
                world,
                clocks,
                numericSchemas,
                events,
                interactions,
                agents,
                knowledge,
                clocksDigest,
                numericsDigest,
                eventsDigest,
                interactionsDigest,
                catalogDigest);
        }

        private static void ValidateTickResourceBudget(
            IEnumerable<NativeWorldEventDefinition> events,
            IEnumerable<NativeWorldClockDefinition> clocks)
        {
            var resources = events
                .SelectMany(
                    item => item.ReadResourceKeys.Concat(
                        item.WriteResourceKeys))
                .Concat(
                    clocks.Select(clock => "clock:" + clock.ClockId))
                .Distinct(StringComparer.Ordinal)
                .Take(WorldValidation.MaximumResourceKeys + 1)
                .Count();
            if (resources > WorldValidation.MaximumResourceKeys)
            {
                throw Limit("$package/events");
            }
        }

        private string FindWorldDefinitionPath()
        {
            var matches = WorldDefinitionPaths
                .Where(_files.ContainsKey)
                .ToArray();
            if (matches.Length == 0)
            {
                throw Error(
                    NativeWorldSemanticReasonCodes.WorldDefinitionMissing,
                    "$package",
                    "The package does not contain world.json.");
            }

            if (matches.Length != 1)
            {
                throw Error(
                    NativeWorldSemanticReasonCodes
                        .AmbiguousWorldDefinition,
                    "$package",
                    "The package contains more than one supported "
                    + "world-definition path.");
            }

            return matches[0];
        }

        private IReadOnlyDictionary<string, string?> ResolveCatalogPaths(
            JsonElement worldRoot,
            string worldPath)
        {
            var explicitPaths = new Dictionary<string, string>(
                StringComparer.Ordinal);
            if (worldRoot.TryGetProperty("catalogs", out var catalogs))
            {
                RequireObject(
                    catalogs,
                    Path(worldPath, "catalogs"),
                    CatalogSpecs.Keys.ToArray());
                foreach (var property in catalogs.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw Shape(
                            Path(
                                worldPath,
                                "catalogs/" + property.Name));
                    }

                    var value = property.Value.GetString();
                    try
                    {
                        explicitPaths.Add(
                            property.Name,
                            WorldArchivePath.Validate(
                                WorldValidation.Required(
                                    value,
                                    property.Name,
                                    _limits.MaxPathUtf8Bytes),
                                _limits.MaxPathUtf8Bytes));
                    }
                    catch (ArgumentException)
                    {
                        throw Error(
                            NativeWorldSemanticReasonCodes.InvalidPath,
                            Path(
                                worldPath,
                                "catalogs/" + property.Name),
                            "A catalog path is invalid.");
                    }
                }
            }

            var directoryIndex = worldPath.LastIndexOf('/');
            var directory = directoryIndex < 0
                ? string.Empty
                : worldPath.Substring(0, directoryIndex + 1);
            var result = new Dictionary<string, string?>(
                StringComparer.Ordinal);
            foreach (var pair in CatalogSpecs)
            {
                if (explicitPaths.TryGetValue(pair.Key, out var explicitPath))
                {
                    if (!_files.ContainsKey(explicitPath))
                    {
                        throw Error(
                            NativeWorldSemanticReasonCodes.FileMissing,
                            Path(
                                worldPath,
                                "catalogs/" + pair.Key),
                            "A declared semantic catalog file is missing.");
                    }

                    result.Add(pair.Key, explicitPath);
                    continue;
                }

                var conventional = directory + pair.Value.DefaultFileName;
                result.Add(
                    pair.Key,
                    _files.ContainsKey(conventional)
                        ? conventional
                        : null);
            }

            return new ReadOnlyDictionary<string, string?>(result);
        }

        private IReadOnlyList<NativeWorldClockDefinition> ReadClocks(
            string? filePath)
        {
            if (filePath is null)
            {
                return Array.Empty<NativeWorldClockDefinition>();
            }

            var root = ReadCatalogRoot(
                filePath,
                NativeWorldSemanticContractIds.ClocksV1,
                "clocks");
            var array = root.GetProperty("clocks");
            if (array.GetArrayLength() > _options.MaxClocks)
            {
                throw Limit(Path(filePath, "clocks"));
            }

            var result = new List<NativeWorldClockDefinition>(
                array.GetArrayLength());
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in array.EnumerateArray())
            {
                var itemPath = Path(filePath, "clocks/" + index);
                RequireObject(
                    item,
                    itemPath,
                    "clockId",
                    "statePath",
                    "initialTick");
                var id = RequiredString(
                    item,
                    "clockId",
                    itemPath,
                    192);
                var statePath = NormalizePointer(
                    RequiredString(
                        item,
                        "statePath",
                        itemPath,
                        1_024),
                    Path(itemPath, "statePath"));
                var initialTick = RequiredCanonicalInt64String(
                    item,
                    "initialTick",
                    itemPath);
                if (!ids.Add(id))
                {
                    throw Duplicate(Path(itemPath, "clockId"));
                }

                if (!paths.Add(statePath))
                {
                    throw Duplicate(Path(itemPath, "statePath"));
                }

                result.Add(
                    new NativeWorldClockDefinition(
                        id,
                        statePath,
                        initialTick));
                index++;
            }

            return new ReadOnlyCollection<NativeWorldClockDefinition>(
                result.OrderBy(item => item.ClockId, StringComparer.Ordinal)
                    .ToArray());
        }

        private IReadOnlyList<WorldNumericSchema> ReadNumericSchemas(
            string? filePath)
        {
            if (filePath is null)
            {
                return Array.Empty<WorldNumericSchema>();
            }

            var root = ReadCatalogRoot(
                filePath,
                NativeWorldSemanticContractIds.NumericsV1,
                "schemas");
            var array = root.GetProperty("schemas");
            if (array.GetArrayLength() > _options.MaxNumericSchemas)
            {
                throw Limit(Path(filePath, "schemas"));
            }

            var result = new List<WorldNumericSchema>(
                array.GetArrayLength());
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in array.EnumerateArray())
            {
                var itemPath = Path(filePath, "schemas/" + index);
                RequireObject(
                    item,
                    itemPath,
                    "schemaId",
                    "scale",
                    "unitId",
                    "minimum",
                    "maximum",
                    "defaultValue");
                var id = RequiredString(
                    item,
                    "schemaId",
                    itemPath,
                    192);
                if (!ids.Add(id))
                {
                    throw Duplicate(Path(itemPath, "schemaId"));
                }

                var scale = RequiredInt32(
                    item,
                    "scale",
                    0,
                    18,
                    itemPath);
                try
                {
                    result.Add(
                        new WorldNumericSchema(
                            id,
                            scale,
                            RequiredString(
                                item,
                                "unitId",
                                itemPath,
                                192),
                            RequiredString(
                                item,
                                "minimum",
                                itemPath,
                                64),
                            RequiredString(
                                item,
                                "maximum",
                                itemPath,
                                64),
                            RequiredString(
                                item,
                                "defaultValue",
                                itemPath,
                                64)));
                }
                catch (ArgumentException)
                {
                    throw Shape(itemPath);
                }

                index++;
            }

            return new ReadOnlyCollection<WorldNumericSchema>(
                result.OrderBy(item => item.SchemaId, StringComparer.Ordinal)
                    .ToArray());
        }

        private IReadOnlyList<NativeWorldEventDefinition> ReadEvents(
            string? filePath,
            IReadOnlyDictionary<string, NativeWorldClockDefinition> clocks,
            IReadOnlyDictionary<string, WorldNumericSchema> numerics)
        {
            if (filePath is null)
            {
                return Array.Empty<NativeWorldEventDefinition>();
            }

            var root = ReadCatalogRoot(
                filePath,
                NativeWorldSemanticContractIds.EventsV1,
                "events");
            var array = root.GetProperty("events");
            if (array.GetArrayLength() > _options.MaxEvents)
            {
                throw Limit(Path(filePath, "events"));
            }

            var result = new List<NativeWorldEventDefinition>(
                array.GetArrayLength());
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in array.EnumerateArray())
            {
                var itemPath = Path(filePath, "events/" + index);
                RequireObject(
                    item,
                    itemPath,
                    "definitionId",
                    "version",
                    "priority",
                    "trigger",
                    "selector",
                    "condition",
                    "effects",
                    "readResourceKeys",
                    "writeResourceKeys");
                var id = RequiredString(
                    item,
                    "definitionId",
                    itemPath,
                    192);
                var version = RequiredString(
                    item,
                    "version",
                    itemPath,
                    96);
                if (!ids.Add(
                        WorldValidation.ComposeStableKey(id, version)))
                {
                    throw Duplicate(Path(itemPath, "definitionId"));
                }

                var priority = RequiredInt32(
                    item,
                    "priority",
                    -1_000_000,
                    1_000_000,
                    itemPath);
                var trigger = ReadTrigger(
                    RequiredProperty(
                        item,
                        "trigger",
                        JsonValueKind.Object,
                        itemPath),
                    Path(itemPath, "trigger"),
                    clocks);
                var selector = ReadSelector(
                    RequiredProperty(
                        item,
                        "selector",
                        JsonValueKind.Object,
                        itemPath),
                    Path(itemPath, "selector"));
                var conditionCounter = 0;
                var condition = ReadCondition(
                    RequiredProperty(
                        item,
                        "condition",
                        JsonValueKind.Object,
                        itemPath),
                    Path(itemPath, "condition"),
                    depth: 0,
                    ref conditionCounter,
                    clocks,
                    numerics);
                var effectsElement = RequiredProperty(
                    item,
                    "effects",
                    JsonValueKind.Array,
                    itemPath);
                if (effectsElement.GetArrayLength()
                    > _options.MaxEffectsPerEvent)
                {
                    throw Limit(Path(itemPath, "effects"));
                }

                var effects = new List<NativeWorldEffect>(
                    effectsElement.GetArrayLength());
                var effectIds = new HashSet<string>(StringComparer.Ordinal);
                var effectIndex = 0;
                foreach (var effectElement in effectsElement.EnumerateArray())
                {
                    var effectPath = Path(
                        itemPath,
                        "effects/" + effectIndex);
                    var effect = ReadEffect(
                        effectElement,
                        effectPath,
                        numerics,
                        allowInteractionTargets: false);
                    if (!effectIds.Add(effect.EffectId))
                    {
                        throw Duplicate(Path(effectPath, "effectId"));
                    }

                    effects.Add(effect);
                    effectIndex++;
                }

                if (!selector.ProducesSubjects
                    && (condition.RequiresSubject
                        || effects.Any(effect => effect.RequiresSubject)))
                {
                    throw Error(
                        NativeWorldSemanticReasonCodes.InvalidSelector,
                        Path(itemPath, "selector"),
                        "The selector does not produce a subject required "
                        + "by this event.");
                }

                var explicitReads = OptionalStringArray(
                    item,
                    "readResourceKeys",
                    itemPath,
                    WorldValidation.MaximumResourceKeys);
                var explicitWrites = OptionalStringArray(
                    item,
                    "writeResourceKeys",
                    itemPath,
                    WorldValidation.MaximumResourceKeys);
                var writes = explicitWrites
                    .Concat(
                        effects.SelectMany(
                            effect => effect.WriteResourceKeys))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                var writeSet = writes.ToHashSet(StringComparer.Ordinal);
                var reads = explicitReads
                    .Concat(
                        effects.SelectMany(
                            effect => effect.ReadResourceKeys))
                    .Where(value => !writeSet.Contains(value))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                if (reads.Length + writes.Length
                    > WorldValidation.MaximumResourceKeys)
                {
                    throw Limit(itemPath);
                }

                result.Add(
                    new NativeWorldEventDefinition(
                        id,
                        version,
                        priority,
                        trigger,
                        selector,
                        condition,
                        effects,
                        reads,
                        writes,
                        DigestElement(item, itemPath)));
                index++;
            }

            return new ReadOnlyCollection<NativeWorldEventDefinition>(
                result.OrderByDescending(item => item.Priority)
                    .ThenBy(item => item.DefinitionId, StringComparer.Ordinal)
                    .ThenBy(item => item.Version, StringComparer.Ordinal)
                    .ToArray());
        }

        private NativeWorldEventTrigger ReadTrigger(
            JsonElement value,
            string path,
            IReadOnlyDictionary<string, NativeWorldClockDefinition> clocks)
        {
            var kind = RequiredString(value, "kind", path, 64);
            switch (kind)
            {
                case "clock":
                    RequireObject(
                        value,
                        path,
                        "kind",
                        "clockId",
                        "everyTicks",
                        "offsetTicks");
                    var clockId = RequiredString(
                        value,
                        "clockId",
                        path,
                        192);
                    if (!clocks.ContainsKey(clockId))
                    {
                        throw Missing(Path(path, "clockId"));
                    }

                    var every = RequiredInt64(
                        value,
                        "everyTicks",
                        1,
                        long.MaxValue,
                        path);
                    var offset = OptionalInt64(
                        value,
                        "offsetTicks",
                        long.MinValue,
                        long.MaxValue,
                        path) ?? 0;
                    return new NativeWorldClockEventTrigger(
                        clockId,
                        every,
                        offset);
                case "event":
                    RequireObject(value, path, "kind", "eventKind");
                    return new NativeWorldEmittedEventTrigger(
                        RequiredString(
                            value,
                            "eventKind",
                            path,
                            192));
                default:
                    throw Error(
                        NativeWorldSemanticReasonCodes.InvalidShape,
                        Path(path, "kind"),
                        "The event trigger kind is unsupported.");
            }
        }

        private NativeWorldParticipantSelector ReadSelector(
            JsonElement value,
            string path)
        {
            var kind = RequiredString(value, "kind", path, 64);
            switch (kind)
            {
                case "singleton":
                    RequireObject(value, path, "kind");
                    return new NativeWorldSingletonSelector();
                case "entity":
                    RequireObject(
                        value,
                        path,
                        "kind",
                        "entityId",
                        "incarnation",
                        "role");
                    return new NativeWorldEntitySelector(
                        RequiredString(
                            value,
                            "entityId",
                            path,
                            192),
                        OptionalInt64(
                            value,
                            "incarnation",
                            0,
                            long.MaxValue,
                            path),
                        OptionalString(value, "role", path, 128)
                        ?? "subject");
                case "entities_by_tag":
                    RequireObject(
                        value,
                        path,
                        "kind",
                        "tag",
                        "maxCandidates",
                        "role");
                    var maximum = RequiredInt32(
                        value,
                        "maxCandidates",
                        1,
                        _options.MaxSelectorCandidates,
                        path);
                    return new NativeWorldTaggedEntitiesSelector(
                        RequiredString(value, "tag", path, 192),
                        maximum,
                        OptionalString(value, "role", path, 128)
                        ?? "subject");
                default:
                    throw Error(
                        NativeWorldSemanticReasonCodes.InvalidSelector,
                        Path(path, "kind"),
                        "The participant selector kind is unsupported.");
            }
        }

        private NativeWorldCondition ReadCondition(
            JsonElement value,
            string path,
            int depth,
            ref int nodeCount,
            IReadOnlyDictionary<string, NativeWorldClockDefinition> clocks,
            IReadOnlyDictionary<string, WorldNumericSchema> numerics)
        {
            if (depth >= _options.MaxConditionDepth
                || ++nodeCount > _options.MaxConditionNodes)
            {
                throw Limit(path);
            }

            var kind = RequiredString(value, "kind", path, 64);
            switch (kind)
            {
                case "always":
                    RequireObject(value, path, "kind");
                    return new NativeWorldAlwaysCondition();
                case "all":
                case "any":
                    RequireObject(value, path, "kind", "conditions");
                    var childrenElement = RequiredProperty(
                        value,
                        "conditions",
                        JsonValueKind.Array,
                        path);
                    if (childrenElement.GetArrayLength() == 0)
                    {
                        throw Condition(path);
                    }

                    var children = new List<NativeWorldCondition>();
                    var index = 0;
                    foreach (var child in childrenElement.EnumerateArray())
                    {
                        if (child.ValueKind != JsonValueKind.Object)
                        {
                            throw Condition(
                                Path(path, "conditions/" + index));
                        }

                        children.Add(
                            ReadCondition(
                                child,
                                Path(path, "conditions/" + index),
                                depth + 1,
                                ref nodeCount,
                                clocks,
                                numerics));
                        index++;
                    }

                    return kind == "all"
                        ? new NativeWorldAllCondition(children)
                        : new NativeWorldAnyCondition(children);
                case "not":
                    RequireObject(value, path, "kind", "condition");
                    return new NativeWorldNotCondition(
                        ReadCondition(
                            RequiredProperty(
                                value,
                                "condition",
                                JsonValueKind.Object,
                                path),
                            Path(path, "condition"),
                            depth + 1,
                            ref nodeCount,
                            clocks,
                            numerics));
                case "path":
                    RequireObject(
                        value,
                        path,
                        "kind",
                        "source",
                        "path",
                        "operator",
                        "value");
                    var pathComparison = ReadComparison(
                        RequiredString(
                            value,
                            "operator",
                            path,
                            32),
                        Path(path, "operator"),
                        allowExistence: true);
                    var hasValue = value.TryGetProperty(
                        "value",
                        out var comparisonValue);
                    return new NativeWorldPathCondition(
                        ReadPathReference(value, path),
                        pathComparison,
                        hasValue ? comparisonValue : null);
                case "tag":
                    RequireObject(value, path, "kind", "tag");
                    return new NativeWorldTagCondition(
                        RequiredString(value, "tag", path, 192));
                case "fixed_point":
                    RequireObject(
                        value,
                        path,
                        "kind",
                        "source",
                        "path",
                        "schemaId",
                        "operator",
                        "value");
                    var schemaId = RequiredString(
                        value,
                        "schemaId",
                        path,
                        192);
                    if (!numerics.TryGetValue(schemaId, out var schema))
                    {
                        throw Missing(Path(path, "schemaId"));
                    }

                    var parsed = WorldFixedPointValue.TryParseCanonical(
                        RequiredString(value, "value", path, 64),
                        schema.Scale);
                    if (!parsed.Succeeded
                        || !schema.TryBind(parsed.Value).Succeeded)
                    {
                        throw Condition(Path(path, "value"));
                    }

                    return new NativeWorldFixedPointCondition(
                        ReadPathReference(value, path),
                        schemaId,
                        ReadComparison(
                            RequiredString(
                                value,
                                "operator",
                                path,
                                32),
                            Path(path, "operator"),
                            allowExistence: false),
                        parsed.Value!);
                case "clock":
                    RequireObject(
                        value,
                        path,
                        "kind",
                        "clockId",
                        "operator",
                        "tick");
                    var clockId = RequiredString(
                        value,
                        "clockId",
                        path,
                        192);
                    if (!clocks.ContainsKey(clockId))
                    {
                        throw Missing(Path(path, "clockId"));
                    }

                    return new NativeWorldClockCondition(
                        clockId,
                        ReadComparison(
                            RequiredString(
                                value,
                                "operator",
                                path,
                                32),
                            Path(path, "operator"),
                            allowExistence: false),
                        RequiredCanonicalInt64String(
                            value,
                            "tick",
                            path));
                default:
                    throw Condition(Path(path, "kind"));
            }
        }

        private NativeWorldPathReference ReadPathReference(
            JsonElement value,
            string path)
        {
            var sourceText = RequiredString(
                value,
                "source",
                path,
                32);
            var source = sourceText switch
            {
                "world" => NativeWorldValueSourceKind.World,
                "subject" => NativeWorldValueSourceKind.Subject,
                "trigger" => NativeWorldValueSourceKind.Trigger,
                _ => throw Condition(Path(path, "source"))
            };
            return new NativeWorldPathReference(
                source,
                NormalizePointer(
                    RequiredString(value, "path", path, 1_024),
                    Path(path, "path")));
        }

        private NativeWorldEffect ReadEffect(
            JsonElement value,
            string path,
            IReadOnlyDictionary<string, WorldNumericSchema> numerics,
            bool allowInteractionTargets)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw Effect(path);
            }

            var kind = RequiredString(value, "kind", path, 64);
            var effectId = RequiredString(
                value,
                "effectId",
                path,
                192);
            switch (kind)
            {
                case "set":
                case "remove":
                    RequireObject(
                        value,
                        path,
                        "kind",
                        "effectId",
                        "entity",
                        "path",
                        "resourceKey",
                        "value");
                    var hasValue = value.TryGetProperty(
                        "value",
                        out var setValue);
                    if ((kind == "set") != hasValue)
                    {
                        throw Effect(path);
                    }

                    return new NativeWorldValueEffect(
                        effectId,
                        ReadEntityReference(
                            RequiredPropertyAny(
                                value,
                                "entity",
                                path),
                            Path(path, "entity"),
                            allowInteractionTargets),
                        RequiredString(value, "path", path, 1_024),
                        RequiredString(
                            value,
                            "resourceKey",
                            path,
                            512),
                        kind == "set"
                            ? WorldValueMutationKind.Set
                            : WorldValueMutationKind.Remove,
                        hasValue ? setValue : null);
                case "numeric":
                    RequireObject(
                        value,
                        path,
                        "kind",
                        "effectId",
                        "entity",
                        "path",
                        "resourceKey",
                        "schemaId",
                        "operation",
                        "value");
                    var numericSchemaId = RequiredString(
                        value,
                        "schemaId",
                        path,
                        192);
                    if (!numerics.TryGetValue(
                            numericSchemaId,
                            out var numericSchema))
                    {
                        throw Missing(Path(path, "schemaId"));
                    }

                    var numericValue =
                        WorldFixedPointValue.TryParseCanonical(
                            RequiredString(
                                value,
                                "value",
                                path,
                                64),
                            numericSchema.Scale);
                    if (!numericValue.Succeeded
                        || !numericSchema.TryBind(
                            numericValue.Value).Succeeded)
                    {
                        throw Effect(Path(path, "value"));
                    }

                    return new NativeWorldNumericEffect(
                        effectId,
                        ReadEntityReference(
                            RequiredPropertyAny(
                                value,
                                "entity",
                                path),
                            Path(path, "entity"),
                            allowInteractionTargets),
                        RequiredString(value, "path", path, 1_024),
                        RequiredString(
                            value,
                            "resourceKey",
                            path,
                            512),
                        numericSchemaId,
                        ReadNumericOperation(
                            RequiredString(
                                value,
                                "operation",
                                path,
                                32),
                            Path(path, "operation")),
                        numericValue.Value!);
                case "transfer":
                    RequireObject(
                        value,
                        path,
                        "kind",
                        "effectId",
                        "source",
                        "sourcePath",
                        "sourceResourceKey",
                        "target",
                        "targetPath",
                        "targetResourceKey",
                        "schemaId",
                        "amount");
                    var transferSchemaId = RequiredString(
                        value,
                        "schemaId",
                        path,
                        192);
                    if (!numerics.TryGetValue(
                            transferSchemaId,
                            out var transferSchema))
                    {
                        throw Missing(Path(path, "schemaId"));
                    }

                    var amount = WorldFixedPointValue.TryParseCanonical(
                        RequiredString(value, "amount", path, 64),
                        transferSchema.Scale);
                    if (!amount.Succeeded
                        || amount.Value!.Units <= 0
                        || !transferSchema.TryBind(amount.Value).Succeeded)
                    {
                        throw Effect(Path(path, "amount"));
                    }

                    return new NativeWorldTransferEffect(
                        effectId,
                        ReadEntityReference(
                            RequiredPropertyAny(value, "source", path),
                            Path(path, "source"),
                            allowInteractionTargets),
                        RequiredString(
                            value,
                            "sourcePath",
                            path,
                            1_024),
                        RequiredString(
                            value,
                            "sourceResourceKey",
                            path,
                            512),
                        ReadEntityReference(
                            RequiredPropertyAny(value, "target", path),
                            Path(path, "target"),
                            allowInteractionTargets),
                        RequiredString(
                            value,
                            "targetPath",
                            path,
                            1_024),
                        RequiredString(
                            value,
                            "targetResourceKey",
                            path,
                            512),
                        transferSchemaId,
                        amount.Value);
                case "relationship":
                    RequireObject(
                        value,
                        path,
                        "kind",
                        "effectId",
                        "source",
                        "target",
                        "relationshipTypeId",
                        "resourceKey",
                        "operation",
                        "value");
                    var operation = RequiredString(
                        value,
                        "operation",
                        path,
                        32);
                    var relationshipKind = operation switch
                    {
                        "upsert" => WorldRelationshipMutationKind.Upsert,
                        "remove" => WorldRelationshipMutationKind.Remove,
                        _ => throw Effect(Path(path, "operation"))
                    };
                    var hasRelationshipValue = value.TryGetProperty(
                        "value",
                        out var relationshipValue);
                    if ((relationshipKind
                         == WorldRelationshipMutationKind.Upsert)
                        != hasRelationshipValue)
                    {
                        throw Effect(path);
                    }

                    return new NativeWorldRelationshipEffect(
                        effectId,
                        ReadEntityReference(
                            RequiredPropertyAny(value, "source", path),
                            Path(path, "source"),
                            allowInteractionTargets),
                        ReadEntityReference(
                            RequiredPropertyAny(value, "target", path),
                            Path(path, "target"),
                            allowInteractionTargets),
                        RequiredString(
                            value,
                            "relationshipTypeId",
                            path,
                            192),
                        RequiredString(
                            value,
                            "resourceKey",
                            path,
                            512),
                        relationshipKind,
                        hasRelationshipValue
                            ? relationshipValue
                            : null);
                case "emit_event":
                    RequireObject(
                        value,
                        path,
                        "kind",
                        "effectId",
                        "eventKind",
                        "payload");
                    var hasPayload = value.TryGetProperty(
                        "payload",
                        out var payload);
                    return new NativeWorldEmitEventEffect(
                        effectId,
                        RequiredString(
                            value,
                            "eventKind",
                            path,
                            192),
                        hasPayload ? payload : null);
                default:
                    throw Effect(Path(path, "kind"));
            }
        }

        private static NativeWorldEntityReference ReadEntityReference(
            JsonElement value,
            string path,
            bool allowInteractionTargets)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                var reference = value.GetString();
                if (string.Equals(
                        reference,
                        "subject",
                        StringComparison.Ordinal))
                {
                    return new NativeWorldSubjectReference();
                }

                const string targetPrefix = "target:";
                if (allowInteractionTargets
                    && reference is not null
                    && reference.StartsWith(
                        targetPrefix,
                        StringComparison.Ordinal)
                    && int.TryParse(
                        reference.Substring(targetPrefix.Length),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var targetIndex)
                    && string.Equals(
                        reference,
                        targetPrefix
                        + targetIndex.ToString(
                            CultureInfo.InvariantCulture),
                        StringComparison.Ordinal)
                    && targetIndex is >= 0 and <= 63)
                {
                    return new NativeWorldInteractionTargetReference(
                        targetIndex);
                }

                throw Effect(path);
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                throw Effect(path);
            }

            RequireObject(
                value,
                path,
                "entityId",
                "incarnation");
            return new NativeWorldLiteralEntityReference(
                RequiredString(value, "entityId", path, 192),
                RequiredInt64(
                    value,
                    "incarnation",
                    0,
                    long.MaxValue,
                    path));
        }

        private static IEnumerable<NativeWorldEntityReference>
            GetEntityReferences(NativeWorldEffect effect)
        {
            switch (effect)
            {
                case NativeWorldValueEffect value:
                    yield return value.Entity;
                    break;
                case NativeWorldNumericEffect numeric:
                    yield return numeric.Entity;
                    break;
                case NativeWorldTransferEffect transfer:
                    yield return transfer.Source;
                    yield return transfer.Target;
                    break;
                case NativeWorldRelationshipEffect relationship:
                    yield return relationship.Source;
                    yield return relationship.Target;
                    break;
            }
        }

        private IReadOnlyList<NativeWorldInteractionDefinition>
            ReadInteractions(
                string? filePath,
                IReadOnlyDictionary<
                    string,
                    NativeWorldClockDefinition> clocks,
                IReadOnlyDictionary<string, WorldNumericSchema> numerics)
        {
            if (filePath is null)
            {
                return Array.Empty<NativeWorldInteractionDefinition>();
            }

            var root = ReadCatalogRoot(
                filePath,
                NativeWorldSemanticContractIds.InteractionsV1,
                "interactions");
            var array = root.GetProperty("interactions");
            if (array.GetArrayLength() > _options.MaxCatalogEntries)
            {
                throw Limit(Path(filePath, "interactions"));
            }

            var result = new List<NativeWorldInteractionDefinition>(
                array.GetArrayLength());
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in array.EnumerateArray())
            {
                var itemPath = Path(
                    filePath,
                    "interactions/" + index);
                RequireObject(
                    item,
                    itemPath,
                    "interactionId",
                    "version",
                    "contentRevision",
                    "priority",
                    "parameterSchemaId",
                    "parameterSchemaVersion",
                    "parameterSchema",
                    "target",
                    "channelIds",
                    "tags",
                    "requiredCapabilities",
                    "availability",
                    "effects",
                    "readResourceKeys",
                    "writeResourceKeys",
                    "presentation");
                var id = RequiredString(
                    item,
                    "interactionId",
                    itemPath,
                    192);
                var version = RequiredString(
                    item,
                    "version",
                    itemPath,
                    96);
                if (!keys.Add(
                        WorldValidation.ComposeStableKey(id, version)))
                {
                    throw Duplicate(Path(itemPath, "interactionId"));
                }

                var contentRevision = RequiredString(
                    item,
                    "contentRevision",
                    itemPath,
                    96);
                var priority = RequiredInt32(
                    item,
                    "priority",
                    -1_000_000,
                    1_000_000,
                    itemPath);
                var parameterSchemaId = RequiredString(
                    item,
                    "parameterSchemaId",
                    itemPath,
                    192);
                var parameterContract =
                    new InteractionParameterContract(
                        parameterSchemaId,
                        RequiredString(
                            item,
                            "parameterSchemaVersion",
                            itemPath,
                            96),
                        RequiredProperty(
                            item,
                            "parameterSchema",
                            JsonValueKind.Object,
                            itemPath));
                InteractionTargetContract? target = null;
                if (item.TryGetProperty(
                        "target",
                        out var targetElement))
                {
                    RequireObject(
                        targetElement,
                        Path(itemPath, "target"),
                        "schemaId",
                        "minimumTargets",
                        "maximumTargets");
                    var targetPath = Path(itemPath, "target");
                    var minimumTargets = RequiredInt32(
                        targetElement,
                        "minimumTargets",
                        0,
                        64,
                        targetPath);
                    var maximumTargets = RequiredInt32(
                        targetElement,
                        "maximumTargets",
                        0,
                        64,
                        targetPath);
                    if (maximumTargets < minimumTargets)
                    {
                        throw Shape(targetPath);
                    }

                    target = new InteractionTargetContract(
                        RequiredString(
                            targetElement,
                            "schemaId",
                            targetPath,
                            192),
                        minimumTargets,
                        maximumTargets);
                }

                var conditionCounter = 0;
                var availability = ReadCondition(
                    RequiredProperty(
                        item,
                        "availability",
                        JsonValueKind.Object,
                        itemPath),
                    Path(itemPath, "availability"),
                    depth: 0,
                    ref conditionCounter,
                    clocks,
                    numerics);
                var effectsElement = RequiredProperty(
                    item,
                    "effects",
                    JsonValueKind.Array,
                    itemPath);
                if (effectsElement.GetArrayLength()
                    > _options.MaxEffectsPerEvent)
                {
                    throw Limit(Path(itemPath, "effects"));
                }

                var effects = new List<NativeWorldEffect>(
                    effectsElement.GetArrayLength());
                var effectIds = new HashSet<string>(StringComparer.Ordinal);
                var effectIndex = 0;
                foreach (var effectElement in effectsElement.EnumerateArray())
                {
                    var effectPath = Path(
                        itemPath,
                        "effects/" + effectIndex);
                    var effect = ReadEffect(
                        effectElement,
                        effectPath,
                        numerics,
                        allowInteractionTargets: true);
                    if (effect is NativeWorldEmitEventEffect)
                    {
                        throw Error(
                            NativeWorldSemanticReasonCodes.InvalidEffect,
                            effectPath,
                            "Direct interactions cannot emit a child event "
                            + "in the initial portable profile.");
                    }

                    if (!effectIds.Add(effect.EffectId))
                    {
                        throw Duplicate(Path(effectPath, "effectId"));
                    }

                    effects.Add(effect);
                    effectIndex++;
                }

                if (effects.Count == 0)
                {
                    throw Effect(Path(itemPath, "effects"));
                }

                var targetReferences = effects
                    .SelectMany(GetEntityReferences)
                    .OfType<NativeWorldInteractionTargetReference>();
                if (targetReferences.Any(
                        reference => target is null
                                     || reference.TargetIndex
                                     >= target.MinimumTargets))
                {
                    throw Error(
                        NativeWorldSemanticReasonCodes.InvalidEffect,
                        Path(itemPath, "effects"),
                        "An interaction effect references a target that "
                        + "is not guaranteed by the declared minimum "
                        + "target count.");
                }

                var explicitReads = OptionalStringArray(
                    item,
                    "readResourceKeys",
                    itemPath,
                    WorldValidation.MaximumResourceKeys);
                var explicitWrites = OptionalStringArray(
                    item,
                    "writeResourceKeys",
                    itemPath,
                    WorldValidation.MaximumResourceKeys);
                var writes = explicitWrites
                    .Concat(
                        effects.SelectMany(
                            effect => effect.WriteResourceKeys))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                var writeSet = writes.ToHashSet(StringComparer.Ordinal);
                var reads = explicitReads
                    .Concat(
                        effects.SelectMany(
                            effect => effect.ReadResourceKeys))
                    .Where(value => !writeSet.Contains(value))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                if (reads.Length + writes.Length
                    > WorldValidation.MaximumResourceKeys)
                {
                    throw Limit(itemPath);
                }

                var digest = DigestElement(item, itemPath);
                var handlerStem = NativeWorldIdentity.Derive(
                    "native.interaction",
                    id,
                    version,
                    digest);
                var emptyParameters = EmptyObject();
                var steps = effects.Select(
                        effect => new InteractionStepDefinition(
                            effect.EffectId,
                            handlerStem + ".effect",
                            emptyParameters,
                            effect.ReadResourceKeys,
                            effect.WriteResourceKeys))
                    .ToArray();
                var details = new InteractionDefinitionDetails(
                    contentRevision,
                    parameterContract,
                    target,
                    OptionalStringArray(
                        item,
                        "channelIds",
                        itemPath,
                        64),
                    OptionalStringArray(
                        item,
                        "tags",
                        itemPath,
                        64),
                    OptionalStringArray(
                        item,
                        "requiredCapabilities",
                        itemPath,
                        64),
                    steps: steps,
                    presentation: ReadStringMap(
                        item,
                        "presentation",
                        itemPath));
                var definition = new InteractionDefinition(
                    id,
                    version,
                    parameterSchemaId,
                    priority,
                    handlerStem + ".availability",
                    handlerStem + ".admission",
                    handlerStem + ".selector",
                    handlerStem + ".resolver",
                    handlerStem + ".effect",
                    readResourceKeys: reads,
                    writeResourceKeys: writes,
                    details: details);
                result.Add(
                    new NativeWorldInteractionDefinition(
                        definition,
                        availability,
                        effects,
                        digest));
                index++;
            }

            return new ReadOnlyCollection<NativeWorldInteractionDefinition>(
                result.OrderBy(
                        item => item.Definition.InteractionId,
                        StringComparer.Ordinal)
                    .ThenBy(
                        item => item.Definition.Version,
                        StringComparer.Ordinal)
                    .ToArray());
        }

        private NativeWorldContentCatalog ReadContentCatalog(
            string catalogKind,
            string? filePath)
        {
            var spec = CatalogSpecs[catalogKind];
            if (filePath is null)
            {
                return new NativeWorldContentCatalog(
                    catalogKind,
                    Array.Empty<NativeWorldContentEntry>(),
                    DigestEmptyCatalog(catalogKind));
            }

            var root = ReadCatalogRoot(
                filePath,
                spec.Contract,
                spec.ArrayProperty);
            var array = root.GetProperty(spec.ArrayProperty);
            if (array.GetArrayLength() > _options.MaxCatalogEntries)
            {
                throw Limit(Path(filePath, spec.ArrayProperty));
            }

            var entries = new List<NativeWorldContentEntry>(
                array.GetArrayLength());
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in array.EnumerateArray())
            {
                var itemPath = Path(
                    filePath,
                    spec.ArrayProperty + "/" + index);
                RequireObject(
                    item,
                    itemPath,
                    "id",
                    "version",
                    "data");
                var id = RequiredString(item, "id", itemPath, 192);
                var version = RequiredString(
                    item,
                    "version",
                    itemPath,
                    96);
                if (!keys.Add(
                        WorldValidation.ComposeStableKey(id, version)))
                {
                    throw Duplicate(Path(itemPath, "id"));
                }

                var data = RequiredPropertyAny(item, "data", itemPath);
                entries.Add(
                    new NativeWorldContentEntry(
                        id,
                        version,
                        data,
                        DigestElement(item, itemPath)));
                index++;
            }

            var ordered = entries
                .OrderBy(item => item.EntryId, StringComparer.Ordinal)
                .ThenBy(item => item.Version, StringComparer.Ordinal)
                .ToArray();
            return new NativeWorldContentCatalog(
                catalogKind,
                ordered,
                DigestContentCatalog(catalogKind, ordered));
        }

        private JsonElement ReadCatalogRoot(
            string filePath,
            string contract,
            string arrayProperty)
        {
            var root = ReadJson(filePath);
            RequireObject(
                root,
                filePath,
                "contract",
                arrayProperty,
                "extensions");
            RequireContract(root, contract, filePath);
            _ = RequiredProperty(
                root,
                arrayProperty,
                JsonValueKind.Array,
                filePath);
            ValidateExtensionsObject(root, filePath);
            return root;
        }

        private JsonElement ReadJson(string filePath)
        {
            if (!_files.TryGetValue(filePath, out var file))
            {
                throw Error(
                    NativeWorldSemanticReasonCodes.FileMissing,
                    filePath,
                    "A semantic package file is missing.");
            }

            if (!IsJsonMediaType(file.MediaType))
            {
                throw Error(
                    NativeWorldSemanticReasonCodes.InvalidMediaType,
                    filePath,
                    "A semantic package file must use a JSON media type.");
            }

            using var document = WorldDataJson.Parse(
                file.ContentSpan,
                _limits,
                filePath);
            return document.RootElement.Clone();
        }

        private static IReadOnlyDictionary<string, long> ReadIncarnations(
            JsonElement root,
            string path)
        {
            if (!root.TryGetProperty(
                    "entityIncarnations",
                    out var value))
            {
                return new ReadOnlyDictionary<string, long>(
                    new Dictionary<string, long>(StringComparer.Ordinal));
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                throw Shape(Path(path, "entityIncarnations"));
            }

            var result = new SortedDictionary<string, long>(
                StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                var text = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : null;
                if (text is null
                    || !long.TryParse(
                        text,
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out var incarnation)
                    || incarnation < 0
                    || !string.Equals(
                        text,
                        incarnation.ToString(
                            CultureInfo.InvariantCulture),
                        StringComparison.Ordinal))
                {
                    throw Shape(
                        Path(
                            path,
                            "entityIncarnations/" + property.Name));
                }

                try
                {
                    result.Add(
                        WorldValidation.Required(
                            property.Name,
                            nameof(property.Name),
                            192),
                        incarnation);
                }
                catch (ArgumentException)
                {
                    throw Shape(Path(path, "entityIncarnations"));
                }
            }

            if (result.Count > WorldValidation.MaximumParticipants)
            {
                throw Limit(Path(path, "entityIncarnations"));
            }

            return new ReadOnlyDictionary<string, long>(
                new Dictionary<string, long>(
                    result,
                    StringComparer.Ordinal));
        }

        private static JsonElement SeedClockState(
            JsonElement initialState,
            IEnumerable<NativeWorldClockDefinition> clocks,
            string worldPath)
        {
            var root = JsonNode.Parse(initialState.GetRawText());
            if (root is not JsonObject)
            {
                throw Shape(Path(worldPath, "initialState"));
            }

            foreach (var clock in clocks)
            {
                var canonical = clock.InitialTick.ToString(
                    CultureInfo.InvariantCulture);
                if (WorldJsonTree.TryGet(
                        root,
                        clock.StatePath,
                        out var existing))
                {
                    if (existing is not JsonValue value
                        || !value.TryGetValue<string>(out var text)
                        || !string.Equals(
                            text,
                            canonical,
                            StringComparison.Ordinal))
                    {
                        throw Error(
                            NativeWorldSemanticReasonCodes
                                .InvalidInitialState,
                            Path(worldPath, "initialState"),
                            "Initial clock state conflicts with its "
                            + "declaration.");
                    }

                    continue;
                }

                try
                {
                    WorldJsonTree.Set(
                        root,
                        clock.StatePath,
                        JsonValue.Create(canonical),
                        createParents: true);
                }
                catch (WorldJsonMutationException)
                {
                    throw Error(
                        NativeWorldSemanticReasonCodes
                            .InvalidInitialState,
                        Path(worldPath, "initialState"),
                        "A clock path cannot be created in initial state.");
                }
            }

            using var document = JsonDocument.Parse(root.ToJsonString());
            var result = document.RootElement.Clone();
            WorldAuthoritativeStateSnapshot.ValidateState(
                result,
                nameof(initialState));
            return result;
        }

        private string DigestWorld(
            string worldId,
            string timelineId,
            string entityRoot,
            string relationshipRoot,
            JsonElement initialState,
            IReadOnlyDictionary<string, long> incarnations)
        {
            return Digest(
                writer =>
                {
                    writer.WriteString("worldId", worldId);
                    writer.WriteString(
                        "defaultTimelineId",
                        timelineId);
                    writer.WriteString(
                        "entityStateRootPath",
                        entityRoot);
                    writer.WriteString(
                        "relationshipRootPath",
                        relationshipRoot);
                    writer.WritePropertyName("initialState");
                    initialState.WriteTo(writer);
                    writer.WritePropertyName("entityIncarnations");
                    writer.WriteStartObject();
                    foreach (var pair in incarnations)
                    {
                        writer.WriteString(
                            pair.Key,
                            pair.Value.ToString(
                                CultureInfo.InvariantCulture));
                    }

                    writer.WriteEndObject();
                });
        }

        private string DigestClocks(
            IEnumerable<NativeWorldClockDefinition> clocks)
        {
            return Digest(
                writer =>
                {
                    writer.WritePropertyName("clocks");
                    writer.WriteStartArray();
                    foreach (var clock in clocks)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("clockId", clock.ClockId);
                        writer.WriteString("statePath", clock.StatePath);
                        writer.WriteString(
                            "initialTick",
                            clock.InitialTick.ToString(
                                CultureInfo.InvariantCulture));
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                });
        }

        private string DigestNumerics(
            IEnumerable<WorldNumericSchema> schemas)
        {
            return Digest(
                writer =>
                {
                    writer.WritePropertyName("schemas");
                    writer.WriteStartArray();
                    foreach (var schema in schemas)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("schemaId", schema.SchemaId);
                        writer.WriteNumber("scale", schema.Scale);
                        writer.WriteString("unitId", schema.UnitId);
                        writer.WriteString(
                            "minimum",
                            schema.Minimum.CanonicalUnits);
                        writer.WriteString(
                            "maximum",
                            schema.Maximum.CanonicalUnits);
                        writer.WriteString(
                            "defaultValue",
                            schema.DefaultValue.CanonicalUnits);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                });
        }

        private string DigestEvents(
            IEnumerable<NativeWorldEventDefinition> events)
        {
            return Digest(
                writer =>
                {
                    writer.WritePropertyName("events");
                    writer.WriteStartArray();
                    foreach (var definition in events)
                    {
                        writer.WriteStartObject();
                        writer.WriteString(
                            "definitionId",
                            definition.DefinitionId);
                        writer.WriteString(
                            "version",
                            definition.Version);
                        writer.WriteString("digest", definition.Digest);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                });
        }

        private string DigestInteractions(
            IEnumerable<NativeWorldInteractionDefinition> interactions)
        {
            return Digest(
                writer =>
                {
                    writer.WritePropertyName("interactions");
                    writer.WriteStartArray();
                    foreach (var interaction in interactions)
                    {
                        writer.WriteStartObject();
                        writer.WriteString(
                            "interactionId",
                            interaction.Definition.InteractionId);
                        writer.WriteString(
                            "version",
                            interaction.Definition.Version);
                        writer.WriteString(
                            "digest",
                            interaction.Digest);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                });
        }

        private string DigestContentCatalog(
            string kind,
            IEnumerable<NativeWorldContentEntry> entries)
        {
            return Digest(
                writer =>
                {
                    writer.WriteString("kind", kind);
                    writer.WritePropertyName("entries");
                    writer.WriteStartArray();
                    foreach (var entry in entries)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", entry.EntryId);
                        writer.WriteString("version", entry.Version);
                        writer.WriteString("digest", entry.Digest);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                });
        }

        private string DigestEmptyCatalog(string kind)
        {
            return DigestContentCatalog(
                kind,
                Array.Empty<NativeWorldContentEntry>());
        }

        private string DigestCatalogSnapshot(
            params string[] componentDigests)
        {
            return Digest(
                writer =>
                {
                    writer.WritePropertyName("componentDigests");
                    writer.WriteStartArray();
                    foreach (var digest in componentDigests)
                    {
                        writer.WriteStringValue(digest);
                    }

                    writer.WriteEndArray();
                });
        }

        private string Digest(Action<Utf8JsonWriter> body)
        {
            using var buffer = new MemoryStream();
            using var boundedBuffer = new WorldBoundedArchiveWriteStream(
                buffer,
                _limits.MaxExpandedBytes,
                WorldDataReasonCodes.ByteLimitExceeded,
                "Native world semantic digest input exceeds its byte limit.");
            using (var writer = new Utf8JsonWriter(boundedBuffer))
            {
                writer.WriteStartObject();
                body(writer);
                writer.WriteEndObject();
            }

            using var document = JsonDocument.Parse(buffer.ToArray());
            return WorldLargeCanonicalJsonDigest.Compute(
                document.RootElement,
                _limits.MaxExpandedBytes,
                "semanticDigest");
        }

        private string DigestElement(JsonElement value, string path)
        {
            try
            {
                return WorldLargeCanonicalJsonDigest.Compute(
                    value,
                    _limits.MaxFileBytes,
                    path);
            }
            catch (ArgumentException exception)
                when (string.Equals(
                    exception.ParamName,
                    path,
                    StringComparison.Ordinal))
            {
                throw new WorldDataContractException(
                    WorldDataReasonCodes.ByteLimitExceeded,
                    "Native world semantic content exceeds its byte limit.");
            }
        }

        private static WorldNumericMutationKind ReadNumericOperation(
            string value,
            string path)
        {
            return value switch
            {
                "set" => WorldNumericMutationKind.Set,
                "add" => WorldNumericMutationKind.Add,
                "subtract" => WorldNumericMutationKind.Subtract,
                "consume" => WorldNumericMutationKind.Consume,
                _ => throw Effect(path)
            };
        }

        private static NativeWorldComparisonOperator ReadComparison(
            string value,
            string path,
            bool allowExistence)
        {
            var result = value switch
            {
                "exists" => NativeWorldComparisonOperator.Exists,
                "missing" => NativeWorldComparisonOperator.Missing,
                "eq" => NativeWorldComparisonOperator.Equal,
                "neq" => NativeWorldComparisonOperator.NotEqual,
                "lt" => NativeWorldComparisonOperator.LessThan,
                "lte" => NativeWorldComparisonOperator.LessThanOrEqual,
                "gt" => NativeWorldComparisonOperator.GreaterThan,
                "gte" => NativeWorldComparisonOperator.GreaterThanOrEqual,
                _ => throw Condition(path)
            };
            if (!allowExistence
                && result is NativeWorldComparisonOperator.Exists
                    or NativeWorldComparisonOperator.Missing)
            {
                throw Condition(path);
            }

            return result;
        }

        private static bool IsJsonMediaType(string mediaType)
        {
            return string.Equals(
                       mediaType,
                       "application/json",
                       StringComparison.OrdinalIgnoreCase)
                   || mediaType.EndsWith(
                       "+json",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static void RequireContract(
            JsonElement root,
            string expected,
            string path)
        {
            var actual = RequiredString(root, "contract", path, 96);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw Error(
                    NativeWorldSemanticReasonCodes.InvalidContract,
                    Path(path, "contract"),
                    "The semantic file contract is unsupported.");
            }
        }

        private static void ValidateExtensionsObject(
            JsonElement root,
            string path)
        {
            if (!root.TryGetProperty("extensions", out var extensions))
            {
                return;
            }

            if (extensions.ValueKind != JsonValueKind.Object)
            {
                throw Shape(Path(path, "extensions"));
            }

            var map = new Dictionary<string, JsonElement>(
                StringComparer.Ordinal);
            foreach (var property in extensions.EnumerateObject())
            {
                map.Add(property.Name, property.Value);
            }

            try
            {
                _ = WorldDataJson.CopyExtensionData(
                    map,
                    nameof(extensions));
            }
            catch (ArgumentException)
            {
                throw Shape(Path(path, "extensions"));
            }
        }

        private static void RequireObject(
            JsonElement value,
            string path,
            params string[] fields)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw Shape(path);
            }

            var known = fields.ToHashSet(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!known.Contains(property.Name))
                {
                    throw Error(
                        NativeWorldSemanticReasonCodes.UnknownField,
                        Path(path, property.Name),
                        "The semantic file contains an unknown field.");
                }
            }
        }

        private static string RequiredString(
            JsonElement parent,
            string propertyName,
            string path,
            int maximumUtf8Bytes)
        {
            if (!parent.TryGetProperty(propertyName, out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                throw Shape(Path(path, propertyName));
            }

            try
            {
                return WorldValidation.Required(
                    value.GetString(),
                    propertyName,
                    maximumUtf8Bytes);
            }
            catch (ArgumentException)
            {
                throw Shape(Path(path, propertyName));
            }
        }

        private static string? OptionalString(
            JsonElement parent,
            string propertyName,
            string path,
            int maximumUtf8Bytes)
        {
            if (!parent.TryGetProperty(propertyName, out var value))
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                throw Shape(Path(path, propertyName));
            }

            return RequiredString(
                parent,
                propertyName,
                path,
                maximumUtf8Bytes);
        }

        private static JsonElement RequiredProperty(
            JsonElement parent,
            string propertyName,
            JsonValueKind kind,
            string path)
        {
            var value = RequiredPropertyAny(
                parent,
                propertyName,
                path);
            if (value.ValueKind != kind)
            {
                throw Shape(Path(path, propertyName));
            }

            return value;
        }

        private static JsonElement RequiredPropertyAny(
            JsonElement parent,
            string propertyName,
            string path)
        {
            if (!parent.TryGetProperty(propertyName, out var value)
                || value.ValueKind == JsonValueKind.Undefined)
            {
                throw Shape(Path(path, propertyName));
            }

            return value;
        }

        private static int RequiredInt32(
            JsonElement parent,
            string propertyName,
            int minimum,
            int maximum,
            string path)
        {
            if (!parent.TryGetProperty(propertyName, out var value)
                || value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt32(out var result)
                || result < minimum
                || result > maximum)
            {
                throw Shape(Path(path, propertyName));
            }

            return result;
        }

        private static long RequiredInt64(
            JsonElement parent,
            string propertyName,
            long minimum,
            long maximum,
            string path)
        {
            var result = RequiredCanonicalInt64String(
                parent,
                propertyName,
                path);
            if (result < minimum || result > maximum)
            {
                throw Shape(Path(path, propertyName));
            }

            return result;
        }

        private static long? OptionalInt64(
            JsonElement parent,
            string propertyName,
            long minimum,
            long maximum,
            string path)
        {
            return parent.TryGetProperty(propertyName, out _)
                ? RequiredInt64(
                    parent,
                    propertyName,
                    minimum,
                    maximum,
                    path)
                : null;
        }

        private static long RequiredCanonicalInt64String(
            JsonElement parent,
            string propertyName,
            string path)
        {
            var text = RequiredString(
                parent,
                propertyName,
                path,
                64);
            if (!long.TryParse(
                    text,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var result)
                || !string.Equals(
                    text,
                    result.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal))
            {
                throw Shape(Path(path, propertyName));
            }

            return result;
        }

        private static IReadOnlyList<string> OptionalStringArray(
            JsonElement parent,
            string propertyName,
            string path,
            int maximum)
        {
            if (!parent.TryGetProperty(propertyName, out var value))
            {
                return Array.Empty<string>();
            }

            if (value.ValueKind != JsonValueKind.Array
                || value.GetArrayLength() > maximum)
            {
                throw Shape(Path(path, propertyName));
            }

            var result = new List<string>(value.GetArrayLength());
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw Shape(
                        Path(path, propertyName + "/" + index));
                }

                try
                {
                    result.Add(
                        WorldValidation.Required(
                            item.GetString(),
                            propertyName,
                            512));
                }
                catch (ArgumentException)
                {
                    throw Shape(
                        Path(path, propertyName + "/" + index));
                }

                index++;
            }

            if (result.Distinct(StringComparer.Ordinal).Count()
                != result.Count)
            {
                throw Duplicate(Path(path, propertyName));
            }

            return new ReadOnlyCollection<string>(result);
        }

        private static IReadOnlyDictionary<string, string> ReadStringMap(
            JsonElement parent,
            string propertyName,
            string path)
        {
            if (!parent.TryGetProperty(propertyName, out var value))
            {
                return new ReadOnlyDictionary<string, string>(
                    new Dictionary<string, string>(
                        StringComparer.Ordinal));
            }

            if (value.ValueKind != JsonValueKind.Object
                || value.EnumerateObject().Count()
                > WorldValidation.MaximumParameters)
            {
                throw Shape(Path(path, propertyName));
            }

            var result = new SortedDictionary<string, string>(
                StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw Shape(
                        Path(
                            path,
                            propertyName + "/" + property.Name));
                }

                try
                {
                    result.Add(
                        WorldValidation.Required(
                            property.Name,
                            propertyName,
                            192),
                        WorldValidation.Required(
                            property.Value.GetString(),
                            propertyName,
                            2_048));
                }
                catch (ArgumentException)
                {
                    throw Shape(Path(path, propertyName));
                }
            }

            return new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(
                    result,
                    StringComparer.Ordinal));
        }

        private static JsonElement EmptyObject()
        {
            using var document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }

        private static string NormalizePointer(string value, string path)
        {
            try
            {
                return WorldJsonPointer.Normalize(value, nameof(value));
            }
            catch (ArgumentException)
            {
                throw Error(
                    NativeWorldSemanticReasonCodes.InvalidPath,
                    path,
                    "A semantic JSON pointer is invalid.");
            }
        }

        private static string Path(string root, string child)
        {
            return root + "#/" + child;
        }

        private static SemanticCompilationException Shape(string path)
        {
            return Error(
                NativeWorldSemanticReasonCodes.InvalidShape,
                path,
                "A semantic value is missing or has an invalid shape.");
        }

        private static SemanticCompilationException Duplicate(string path)
        {
            return Error(
                NativeWorldSemanticReasonCodes.DuplicateId,
                path,
                "A semantic identifier is duplicated.");
        }

        private static SemanticCompilationException Missing(string path)
        {
            return Error(
                NativeWorldSemanticReasonCodes.ReferenceMissing,
                path,
                "A referenced semantic definition does not exist.");
        }

        private static SemanticCompilationException Condition(string path)
        {
            return Error(
                NativeWorldSemanticReasonCodes.InvalidCondition,
                path,
                "A declarative condition is invalid or unsupported.");
        }

        private static SemanticCompilationException Effect(string path)
        {
            return Error(
                NativeWorldSemanticReasonCodes.InvalidEffect,
                path,
                "A declarative effect is invalid or unsupported.");
        }

        private static SemanticCompilationException Limit(string path)
        {
            return Error(
                NativeWorldSemanticReasonCodes.LimitExceeded,
                path,
                "A semantic collection exceeds its configured limit.");
        }

        private static SemanticCompilationException Error(
            string code,
            string path,
            string message)
        {
            return new SemanticCompilationException(code, path, message);
        }
    }

    private sealed class CatalogSpec
    {
        public CatalogSpec(
            string defaultFileName,
            string contract,
            string arrayProperty)
        {
            DefaultFileName = defaultFileName;
            Contract = contract;
            ArrayProperty = arrayProperty;
        }

        public string DefaultFileName { get; }

        public string Contract { get; }

        public string ArrayProperty { get; }
    }

    private sealed class SemanticCompilationException : Exception
    {
        public SemanticCompilationException(
            string code,
            string path,
            string message)
            : base(message)
        {
            Code = code;
            Path = path;
        }

        public string Code { get; }

        public string Path { get; }
    }
}
