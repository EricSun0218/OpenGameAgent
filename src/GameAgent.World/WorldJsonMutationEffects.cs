using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameAgent.Core;

namespace GameAgent.World;

public static class WorldMutationApplyReasonCodes
{
    public const string Applied = "world_mutation_set_applied";
    public const string BindingMismatch = "world_mutation_binding_mismatch";
    public const string MissingSchema = "world_mutation_schema_missing";
    public const string MissingPath = "world_mutation_path_missing";
    public const string InvalidPath = "world_mutation_path_invalid";
    public const string PathConflict = "world_mutation_path_conflict";
    public const string ValueDigestMismatch =
        "world_mutation_value_digest_mismatch";
    public const string InvalidValue = "world_mutation_value_invalid";
    public const string UndeclaredResource =
        "world_mutation_resource_undeclared";
    public const string UnknownIntent = "world_mutation_intent_unknown";
}

/// <summary>
/// Maps typed entity-relative intents to paths in one game-owned JSON state.
/// Implementations are trusted host code. Package data never supplies one.
/// </summary>
public interface IWorldMutationPathResolver
{
    string ResolveValuePath(
        GameEntityIdentity entity,
        string componentPath);

    string ResolveNumericPath(
        GameEntityIdentity entity,
        string numericPath);

    string ResolveRelationshipPath(
        GameEntityIdentity source,
        GameEntityIdentity target,
        string relationshipTypeId);
}

/// <summary>
/// Safe reference mapping that scopes every component path below the entity
/// selected by its typed identity.
/// </summary>
public sealed class WorldEntityMutationPathResolver
    : IWorldMutationPathResolver
{
    private readonly string _entityRootPath;
    private readonly string _relationshipRootPath;

    public WorldEntityMutationPathResolver(
        string entityRootPath,
        string relationshipRootPath)
    {
        _entityRootPath = WorldJsonPointer.Normalize(
            entityRootPath,
            nameof(entityRootPath));
        _relationshipRootPath = WorldJsonPointer.Normalize(
            relationshipRootPath,
            nameof(relationshipRootPath));
    }

    public string ResolveValuePath(
        GameEntityIdentity entity,
        string componentPath)
    {
        _ = entity ?? throw new ArgumentNullException(nameof(entity));
        return EntityPath(
            entity,
            WorldJsonPointer.Normalize(
            componentPath,
                nameof(componentPath)));
    }

    public string ResolveNumericPath(
        GameEntityIdentity entity,
        string numericPath)
    {
        _ = entity ?? throw new ArgumentNullException(nameof(entity));
        return EntityPath(
            entity,
            WorldJsonPointer.Normalize(
            numericPath,
                nameof(numericPath)));
    }

    public string ResolveRelationshipPath(
        GameEntityIdentity source,
        GameEntityIdentity target,
        string relationshipTypeId)
    {
        _ = source ?? throw new ArgumentNullException(nameof(source));
        _ = target ?? throw new ArgumentNullException(nameof(target));
        var type = WorldValidation.Required(
            relationshipTypeId,
            nameof(relationshipTypeId));
        return string.Concat(
            _relationshipRootPath,
            "/",
            WorldJsonPointer.Escape(source.EntityId),
            "/",
            source.Incarnation.ToString(CultureInfo.InvariantCulture),
            "/",
            WorldJsonPointer.Escape(type),
            "/",
            WorldJsonPointer.Escape(target.EntityId),
            "/",
            target.Incarnation.ToString(CultureInfo.InvariantCulture));
    }

    private string EntityPath(
        GameEntityIdentity entity,
        string relativePath)
    {
        return string.Concat(
            _entityRootPath,
            "/",
            WorldJsonPointer.Escape(entity.EntityId),
            relativePath);
    }
}

/// <summary>
/// Trusted escape hatch for hosts that already own absolute JSON pointers.
/// Never construct this resolver from package input. Prefer
/// <see cref="WorldEntityMutationPathResolver"/> for portable content.
/// </summary>
public sealed class WorldAbsoluteMutationPathResolver
    : IWorldMutationPathResolver
{
    private readonly string _relationshipRootPath;

    public WorldAbsoluteMutationPathResolver(
        string relationshipRootPath)
    {
        _relationshipRootPath = WorldJsonPointer.Normalize(
            relationshipRootPath,
            nameof(relationshipRootPath));
    }

    public string ResolveValuePath(
        GameEntityIdentity entity,
        string componentPath)
    {
        _ = entity ?? throw new ArgumentNullException(nameof(entity));
        return WorldJsonPointer.Normalize(
            componentPath,
            nameof(componentPath));
    }

    public string ResolveNumericPath(
        GameEntityIdentity entity,
        string numericPath)
    {
        _ = entity ?? throw new ArgumentNullException(nameof(entity));
        return WorldJsonPointer.Normalize(
            numericPath,
            nameof(numericPath));
    }

    public string ResolveRelationshipPath(
        GameEntityIdentity source,
        GameEntityIdentity target,
        string relationshipTypeId)
    {
        _ = source ?? throw new ArgumentNullException(nameof(source));
        _ = target ?? throw new ArgumentNullException(nameof(target));
        var type = WorldValidation.Required(
            relationshipTypeId,
            nameof(relationshipTypeId));
        return string.Concat(
            _relationshipRootPath,
            "/",
            WorldJsonPointer.Escape(source.EntityId),
            "/",
            source.Incarnation.ToString(CultureInfo.InvariantCulture),
            "/",
            WorldJsonPointer.Escape(type),
            "/",
            WorldJsonPointer.Escape(target.EntityId),
            "/",
            target.Incarnation.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Optional safe JSON-state implementation for a typed atomic mutation set.
/// It applies every intent to a private tree and replaces the transaction
/// draft only after all intents succeed.
/// </summary>
public sealed class WorldAtomicMutationEffect :
    IWorldTransactionalEventEffect,
    IWorldTransactionalEffectAdmission
{
    private readonly IReadOnlyDictionary<string, WorldNumericSchema>
        _numericSchemas;

    private readonly IWorldMutationPathResolver _paths;

    public WorldAtomicMutationEffect(
        WorldAtomicMutationSet mutationSet,
        IEnumerable<WorldNumericSchema> numericSchemas,
        IWorldMutationPathResolver pathResolver,
        bool allowTrustedAbsolutePaths = false)
    {
        MutationSet = mutationSet
                      ?? throw new ArgumentNullException(
                          nameof(mutationSet));
        _numericSchemas = CopySchemas(numericSchemas);
        _paths = pathResolver
                 ?? throw new ArgumentNullException(nameof(pathResolver));
        if (_paths is WorldAbsoluteMutationPathResolver
            && !allowTrustedAbsolutePaths)
        {
            throw new ArgumentException(
                "Absolute mutation paths require explicit trusted-host "
                + "opt-in.",
                nameof(pathResolver));
        }

        ExpectedIncarnations =
            new ReadOnlyCollection<WorldEntityIncarnationExpectation>(
                CollectIdentities(mutationSet.Intents)
                    .OrderBy(
                        identity => identity.EntityId,
                        StringComparer.Ordinal)
                    .Select(
                        identity =>
                            new WorldEntityIncarnationExpectation(
                                identity.EntityId,
                                identity.Incarnation))
                    .ToArray());
    }

    public WorldAtomicMutationSet MutationSet { get; }

    public string CommandId => MutationSet.CommandId;

    public string OperationId => MutationSet.OperationId;

    public string PayloadDigest => MutationSet.Digest;

    public IReadOnlyList<WorldEntityIncarnationExpectation>
        ExpectedIncarnations
    { get; }

    public ValueTask<WorldEventEffectResult> ApplyAsync(
        WorldTransactionalEventEffectContext context,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bindingFailure = ValidateBinding(context);
        if (bindingFailure is not null)
        {
            return Result(bindingFailure);
        }

        var root = JsonNode.Parse(context.Source.State.GetRawText());
        if (root is not JsonObject)
        {
            return Result(WorldMutationApplyReasonCodes.InvalidValue);
        }

        try
        {
            foreach (var intent in MutationSet.Intents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var failure = ApplyIntent(root, intent);
                if (failure is not null)
                {
                    return Result(failure);
                }
            }

            using var stateDocument = JsonDocument.Parse(
                root.ToJsonString());
            var state = stateDocument.RootElement.Clone();
            WorldAuthoritativeStateSnapshot.ValidateState(
                state,
                nameof(state));
            context.Draft.ReplaceState(state);
            return new ValueTask<WorldEventEffectResult>(
                new WorldEventEffectResult(
                    applied: true,
                    WorldMutationApplyReasonCodes.Applied,
                    BuildTypedResult()));
        }
        catch (WorldJsonMutationException exception)
        {
            return Result(exception.ReasonCode);
        }
        catch (WorldMutationValidationException exception)
        {
            return Result(exception.ReasonCode);
        }
        catch (JsonException)
        {
            return Result(WorldMutationApplyReasonCodes.InvalidValue);
        }
        catch (InvalidOperationException)
        {
            return Result(WorldMutationApplyReasonCodes.InvalidValue);
        }
    }

    private string? ValidateBinding(
        WorldTransactionalEventEffectContext context)
    {
        var coordinate = context.Source.Coordinate;
        if (!string.Equals(
                MutationSet.WorldId,
                coordinate.WorldId,
                StringComparison.Ordinal)
            || !string.Equals(
                MutationSet.TimelineId,
                coordinate.TimelineId,
                StringComparison.Ordinal)
            || MutationSet.TimelineEpoch != coordinate.TimelineEpoch
            || MutationSet.ExpectedSaveRevision
            != coordinate.SaveRevision
            || !string.Equals(
                MutationSet.ExpectedStateVersion,
                coordinate.StateVersion.ToString(
                    CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            || !string.Equals(
                MutationSet.CatalogDigest,
                coordinate.CatalogDigest,
                StringComparison.Ordinal))
        {
            return WorldMutationApplyReasonCodes.BindingMismatch;
        }

        var reads = new HashSet<string>(
            context.Instance.ReadResourceKeys,
            StringComparer.Ordinal);
        reads.UnionWith(context.Instance.WriteResourceKeys);
        var writes = new HashSet<string>(
            context.Instance.WriteResourceKeys,
            StringComparer.Ordinal);
        if (MutationSet.ReadResourceKeys.Any(key => !reads.Contains(key))
            || MutationSet.WriteResourceKeys.Any(
                key => !writes.Contains(key)))
        {
            return WorldMutationApplyReasonCodes.UndeclaredResource;
        }

        foreach (var expectation in ExpectedIncarnations)
        {
            if (!context.Source.TryGetIncarnation(
                    expectation.EntityId,
                    out var actual)
                || actual != expectation.Incarnation)
            {
                return WorldTransactionReasonCodes.StaleIncarnation;
            }
        }

        return null;
    }

    private string? ApplyIntent(JsonNode root, IWorldMutationIntent intent)
    {
        return intent switch
        {
            WorldValueMutationIntent value => ApplyValue(root, value),
            WorldNumericMutationIntent numeric => ApplyNumeric(root, numeric),
            WorldTransferMutationIntent transfer =>
                ApplyTransfer(root, transfer),
            WorldRelationshipMutationIntent relationship =>
                ApplyRelationship(root, relationship),
            _ => WorldMutationApplyReasonCodes.UnknownIntent
        };
    }

    private string? ApplyValue(
        JsonNode root,
        WorldValueMutationIntent intent)
    {
        var path = _paths.ResolveValuePath(
            intent.Entity,
            intent.ComponentPath);
        if (intent.ExpectedValueDigest is not null)
        {
            if (!WorldJsonTree.TryGet(root, path, out var current)
                || !string.Equals(
                    Digest(current),
                    intent.ExpectedValueDigest,
                    StringComparison.Ordinal))
            {
                return WorldMutationApplyReasonCodes.ValueDigestMismatch;
            }
        }

        if (intent.MutationKind == WorldValueMutationKind.Remove)
        {
            return WorldJsonTree.Remove(root, path)
                ? null
                : WorldMutationApplyReasonCodes.MissingPath;
        }

        WorldJsonTree.Set(
            root,
            path,
            ParseNode(intent.Value!.Value),
            createParents: true);
        return null;
    }

    private string? ApplyNumeric(
        JsonNode root,
        WorldNumericMutationIntent intent)
    {
        if (!_numericSchemas.TryGetValue(
                intent.NumericSchemaId,
                out var schema))
        {
            return WorldMutationApplyReasonCodes.MissingSchema;
        }

        var path = _paths.ResolveNumericPath(
            intent.Entity,
            intent.NumericPath);
        WorldNumericOperationResult operation;
        if (intent.MutationKind == WorldNumericMutationKind.Set)
        {
            var binding = schema.TryBind(intent.Operand);
            if (!binding.Succeeded)
            {
                return binding.ReasonCode;
            }

            operation = WorldNumericOperationResult.Success(
                binding.Quantity!);
        }
        else
        {
            if (!TryReadNumeric(
                    root,
                    path,
                    schema,
                    out var current,
                    out var failure))
            {
                return failure;
            }

            var operand = schema.TryBind(intent.Operand);
            if (!operand.Succeeded)
            {
                return operand.ReasonCode;
            }

            operation = intent.MutationKind switch
            {
                WorldNumericMutationKind.Add => WorldNumericMath.Add(
                    current!,
                    operand.Quantity!,
                    schema),
                WorldNumericMutationKind.Subtract =>
                    WorldNumericMath.Subtract(
                        current!,
                        operand.Quantity!,
                        schema),
                WorldNumericMutationKind.Consume =>
                    WorldNumericMath.Consume(
                        current!,
                        operand.Quantity!),
                _ => throw new InvalidOperationException(
                    "The numeric mutation kind is invalid.")
            };
        }

        if (!operation.Succeeded)
        {
            return operation.ReasonCode;
        }

        WorldJsonTree.Set(
            root,
            path,
            JsonValue.Create(operation.Quantity!.Value.CanonicalUnits),
            createParents: true);
        return null;
    }

    private string? ApplyTransfer(
        JsonNode root,
        WorldTransferMutationIntent intent)
    {
        if (!_numericSchemas.TryGetValue(
                intent.NumericSchemaId,
                out var schema))
        {
            return WorldMutationApplyReasonCodes.MissingSchema;
        }

        var sourcePath = _paths.ResolveNumericPath(
            intent.Source,
            intent.SourceNumericPath);
        var targetPath = _paths.ResolveNumericPath(
            intent.Target,
            intent.TargetNumericPath);
        if (string.Equals(
                sourcePath,
                targetPath,
                StringComparison.Ordinal))
        {
            return WorldMutationApplyReasonCodes.PathConflict;
        }

        if (!TryReadNumeric(
                root,
                sourcePath,
                schema,
                out var source,
                out var failure)
            || !TryReadNumeric(
                root,
                targetPath,
                schema,
                out var target,
                out failure))
        {
            return failure;
        }

        var amount = schema.TryBind(intent.Amount);
        if (!amount.Succeeded)
        {
            return amount.ReasonCode;
        }

        var debit = WorldNumericMath.Consume(
            source!,
            amount.Quantity!);
        if (!debit.Succeeded)
        {
            return debit.ReasonCode;
        }

        var credit = WorldNumericMath.Add(
            target!,
            amount.Quantity!,
            schema);
        if (!credit.Succeeded)
        {
            return credit.ReasonCode;
        }

        WorldJsonTree.Set(
            root,
            sourcePath,
            JsonValue.Create(debit.Quantity!.Value.CanonicalUnits),
            createParents: false);
        WorldJsonTree.Set(
            root,
            targetPath,
            JsonValue.Create(credit.Quantity!.Value.CanonicalUnits),
            createParents: false);
        return null;
    }

    private string? ApplyRelationship(
        JsonNode root,
        WorldRelationshipMutationIntent intent)
    {
        var path = _paths.ResolveRelationshipPath(
            intent.Source,
            intent.Target,
            intent.RelationshipTypeId);
        if (intent.MutationKind
            == WorldRelationshipMutationKind.Remove)
        {
            return WorldJsonTree.Remove(root, path)
                ? null
                : WorldMutationApplyReasonCodes.MissingPath;
        }

        WorldJsonTree.Set(
            root,
            path,
            ParseNode(intent.Value!.Value),
            createParents: true);
        return null;
    }

    private static bool TryReadNumeric(
        JsonNode root,
        string path,
        WorldNumericSchema schema,
        out WorldNumericQuantity? quantity,
        out string reasonCode)
    {
        quantity = null;
        if (!WorldJsonTree.TryGet(root, path, out var node)
            || node is not JsonValue value
            || !value.TryGetValue<string>(out var canonical))
        {
            reasonCode = WorldMutationApplyReasonCodes.InvalidValue;
            return false;
        }

        var parsed = WorldFixedPointValue.TryParseCanonical(
            canonical,
            schema.Scale);
        var binding = parsed.Succeeded
            ? schema.TryBind(parsed.Value)
            : WorldNumericBindingResult.Failure(parsed.ReasonCode);
        if (!binding.Succeeded)
        {
            reasonCode = binding.ReasonCode;
            return false;
        }

        quantity = binding.Quantity;
        reasonCode = string.Empty;
        return true;
    }

    private JsonElement BuildTypedResult()
    {
        using var document = JsonDocument.Parse(
            string.Concat(
                "{\"mutationSetDigest\":\"",
                MutationSet.Digest,
                "\",\"intentCount\":\"",
                MutationSet.Intents.Count.ToString(
                    CultureInfo.InvariantCulture),
                "\"}"));
        return document.RootElement.Clone();
    }

    private static JsonNode? ParseNode(JsonElement value)
    {
        return JsonNode.Parse(value.GetRawText());
    }

    private static string Digest(JsonNode? node)
    {
        using var document = JsonDocument.Parse(
            node?.ToJsonString() ?? "null");
        return WorldLargeCanonicalJsonDigest.Compute(
            document.RootElement,
            8L * 1024 * 1024,
            "value");
    }

    private static ValueTask<WorldEventEffectResult> Result(
        string reasonCode)
    {
        return new ValueTask<WorldEventEffectResult>(
            new WorldEventEffectResult(
                applied: false,
                reasonCode));
    }

    private static IReadOnlyDictionary<string, WorldNumericSchema>
        CopySchemas(IEnumerable<WorldNumericSchema> schemas)
    {
        if (schemas is null)
        {
            throw new ArgumentNullException(nameof(schemas));
        }

        var values = WorldValidation.MaterializeBounded(
            schemas,
            WorldValidation.MaximumNumericSchemas,
            nameof(schemas));
        var copy = new SortedDictionary<string, WorldNumericSchema>(
            StringComparer.Ordinal);
        foreach (var schema in values)
        {
            if (schema is null)
            {
                throw new ArgumentException(
                    "Numeric schemas cannot contain null entries.",
                    nameof(schemas));
            }

            if (!copy.TryAdd(schema.SchemaId, schema))
            {
                throw new ArgumentException(
                    "Numeric schema identifiers must be unique.",
                    nameof(schemas));
            }
        }

        return new ReadOnlyDictionary<string, WorldNumericSchema>(
            new Dictionary<string, WorldNumericSchema>(
                copy,
                StringComparer.Ordinal));
    }

    private static IReadOnlyList<GameEntityIdentity> CollectIdentities(
        IEnumerable<IWorldMutationIntent> intents)
    {
        var identities = new SortedDictionary<string, GameEntityIdentity>(
            StringComparer.Ordinal);
        foreach (var intent in intents)
        {
            switch (intent)
            {
                case WorldValueMutationIntent value:
                    Add(value.Entity);
                    break;
                case WorldNumericMutationIntent numeric:
                    Add(numeric.Entity);
                    break;
                case WorldTransferMutationIntent transfer:
                    Add(transfer.Source);
                    Add(transfer.Target);
                    break;
                case WorldRelationshipMutationIntent relationship:
                    Add(relationship.Source);
                    Add(relationship.Target);
                    break;
            }
        }

        return new ReadOnlyCollection<GameEntityIdentity>(
            identities.Values.ToArray());

        void Add(GameEntityIdentity identity)
        {
            if (identities.TryGetValue(
                    identity.EntityId,
                    out var existing)
                && existing.Incarnation != identity.Incarnation)
            {
                throw new ArgumentException(
                    "A mutation set cannot bind one entity to multiple "
                    + "incarnations.",
                    nameof(intents));
            }

            identities[identity.EntityId] = identity;
        }
    }
}

internal sealed class WorldJsonMutationException : Exception
{
    public WorldJsonMutationException(string reasonCode)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

internal static class WorldJsonTree
{
    public static bool TryGet(
        JsonNode root,
        string pointer,
        out JsonNode? value)
    {
        var segments = WorldJsonPointer.Parse(pointer);
        JsonNode? current = root;
        foreach (var segment in segments)
        {
            if (current is not JsonObject currentObject
                || !currentObject.TryGetPropertyValue(
                    segment,
                    out current))
            {
                value = null;
                return false;
            }
        }

        value = current;
        return true;
    }

    public static void Set(
        JsonNode root,
        string pointer,
        JsonNode? value,
        bool createParents)
    {
        var (parent, property) = ResolveParent(
            root,
            pointer,
            createParents);
        parent[property] = value;
    }

    public static bool Remove(JsonNode root, string pointer)
    {
        var (parent, property) = ResolveParent(
            root,
            pointer,
            createParents: false);
        return parent.Remove(property);
    }

    private static (JsonObject Parent, string Property) ResolveParent(
        JsonNode root,
        string pointer,
        bool createParents)
    {
        var segments = WorldJsonPointer.Parse(pointer);
        if (segments.Count == 0)
        {
            throw new WorldJsonMutationException(
                WorldMutationApplyReasonCodes.InvalidPath);
        }

        if (root is not JsonObject current)
        {
            throw new WorldJsonMutationException(
                WorldMutationApplyReasonCodes.InvalidValue);
        }

        for (var index = 0; index < segments.Count - 1; index++)
        {
            var segment = segments[index];
            if (!current.TryGetPropertyValue(segment, out var child))
            {
                if (!createParents)
                {
                    throw new WorldJsonMutationException(
                        WorldMutationApplyReasonCodes.MissingPath);
                }

                var created = new JsonObject();
                current.Add(segment, created);
                current = created;
                continue;
            }

            if (child is not JsonObject childObject)
            {
                throw new WorldJsonMutationException(
                    WorldMutationApplyReasonCodes.InvalidPath);
            }

            current = childObject;
        }

        return (current, segments[^1]);
    }
}

internal static class WorldJsonPointer
{
    public static string Normalize(string pointer, string parameterName)
    {
        var value = WorldValidation.Required(
            pointer,
            parameterName,
            1_024);
        try
        {
            _ = Parse(value);
        }
        catch (WorldJsonMutationException exception)
        {
            throw new WorldMutationValidationException(
                exception.ReasonCode,
                "The value is not a supported absolute JSON pointer.",
                parameterName);
        }

        return value;
    }

    public static IReadOnlyList<string> Parse(string pointer)
    {
        if (string.IsNullOrEmpty(pointer)
            || pointer[0] != '/')
        {
            throw new WorldJsonMutationException(
                WorldMutationApplyReasonCodes.InvalidPath);
        }

        var raw = pointer.Split('/').Skip(1);
        var result = new List<string>();
        foreach (var segment in raw)
        {
            var decoded = new System.Text.StringBuilder(segment.Length);
            for (var index = 0; index < segment.Length; index++)
            {
                if (segment[index] != '~')
                {
                    decoded.Append(segment[index]);
                    continue;
                }

                if (index + 1 >= segment.Length)
                {
                    throw new WorldJsonMutationException(
                        WorldMutationApplyReasonCodes.InvalidPath);
                }

                var escaped = segment[++index];
                decoded.Append(
                    escaped switch
                    {
                        '0' => '~',
                        '1' => '/',
                        _ => throw new WorldJsonMutationException(
                            WorldMutationApplyReasonCodes.InvalidPath)
                    });
            }

            result.Add(decoded.ToString());
        }

        return new ReadOnlyCollection<string>(result);
    }

    public static string Escape(string value)
    {
        return value
            .Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);
    }
}
