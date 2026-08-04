using System.Collections.ObjectModel;
using System.Text.Json;

namespace GameAgent.Workflow;

public enum WorkflowStageKind
{
    Step = 0,
    Foreach = 1,
    Reduce = 2,
    Loop = 3
}

public static class WorkflowReasonCodes
{
    public const string DefinitionInvalid = "workflow_definition_invalid";
    public const string DuplicateStageId = "workflow_duplicate_stage_id";
    public const string UnknownDependency = "workflow_unknown_dependency";
    public const string SelfDependency = "workflow_self_dependency";
    public const string DependencyCycle = "workflow_dependency_cycle";
    public const string OutputStageInvalid = "workflow_output_stage_invalid";
    public const string StageShapeInvalid = "workflow_stage_shape_invalid";
    public const string SchemaInvalid = "workflow_schema_invalid";
    public const string SchemaMismatch = "workflow_schema_mismatch";
    public const string JsonLimitExceeded = "workflow_json_limit_exceeded";
    public const string LimitExceeded = "workflow_limit_exceeded";
    public const string ExecutorMissing = "workflow_executor_missing";
    public const string ExecutorFailed = "workflow_executor_failed";
    public const string ExecutorInterrupted = "workflow_executor_interrupted";
    public const string ForeachSourceInvalid = "workflow_foreach_source_invalid";
    public const string ForeachIdentityInvalid = "workflow_foreach_identity_invalid";
    public const string ForeachIdentityCollision = "workflow_foreach_identity_collision";
    public const string LoopConditionInvalid = "workflow_loop_condition_invalid";
    public const string LoopIterationLimit = "workflow_loop_iteration_limit";
    public const string RevisionConflict = "workflow_revision_conflict";
    public const string LeaseUnavailable = "workflow_lease_unavailable";
    public const string LeaseLost = "workflow_lease_lost";
    public const string GenerationFenced = "workflow_generation_fenced";
    public const string CancellationRequested = "workflow_cancellation_requested";
    public const string DefinitionMismatch = "workflow_definition_mismatch";
    public const string InputMismatch = "workflow_input_mismatch";
    public const string RunNotFound = "workflow_run_not_found";
    public const string RunAlreadyExists = "workflow_run_already_exists";
    public const string StoreCapacityExceeded = "workflow_store_capacity_exceeded";
}

public sealed class WorkflowLimits
{
    public WorkflowLimits(
        int maxStages = 128,
        int maxDependenciesPerStage = 32,
        int maxParallelism = 8,
        int maxForeachItems = 256,
        int maxLoopIterations = 64,
        int maxStageExecutions = 4_096,
        int maxStageAttempts = 8,
        int maxInputBytes = 131_072,
        int maxStageOutputBytes = 131_072,
        int maxRetainedOutputBytes = 2_097_152,
        long maxDurationMs = 600_000,
        int maxSchemaDepth = 16,
        int maxSchemaBytes = 131_072)
    {
        MaxStages = InRange(maxStages, 1, 4_096, nameof(maxStages));
        MaxDependenciesPerStage = InRange(
            maxDependenciesPerStage,
            0,
            1_024,
            nameof(maxDependenciesPerStage));
        MaxParallelism = InRange(
            maxParallelism,
            1,
            256,
            nameof(maxParallelism));
        MaxForeachItems = InRange(
            maxForeachItems,
            1,
            16_384,
            nameof(maxForeachItems));
        MaxLoopIterations = InRange(
            maxLoopIterations,
            1,
            16_384,
            nameof(maxLoopIterations));
        MaxStageExecutions = InRange(
            maxStageExecutions,
            1,
            1_000_000,
            nameof(maxStageExecutions));
        MaxStageAttempts = InRange(
            maxStageAttempts,
            1,
            128,
            nameof(maxStageAttempts));
        MaxInputBytes = InRange(
            maxInputBytes,
            1,
            262_144,
            nameof(maxInputBytes));
        MaxStageOutputBytes = InRange(
            maxStageOutputBytes,
            1,
            262_144,
            nameof(maxStageOutputBytes));
        MaxRetainedOutputBytes = InRange(
            maxRetainedOutputBytes,
            maxStageOutputBytes,
            67_108_864,
            nameof(maxRetainedOutputBytes));
        if (maxDurationMs is < 1 or > 86_400_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDurationMs));
        }

        MaxDurationMs = maxDurationMs;
        MaxSchemaDepth = InRange(
            maxSchemaDepth,
            1,
            64,
            nameof(maxSchemaDepth));
        MaxSchemaBytes = InRange(
            maxSchemaBytes,
            1_024,
            262_144,
            nameof(maxSchemaBytes));
    }

    public int MaxStages { get; }

    public int MaxDependenciesPerStage { get; }

    public int MaxParallelism { get; }

    public int MaxForeachItems { get; }

    public int MaxLoopIterations { get; }

    public int MaxStageExecutions { get; }

    public int MaxStageAttempts { get; }

    public int MaxInputBytes { get; }

    public int MaxStageOutputBytes { get; }

    public int MaxRetainedOutputBytes { get; }

    public long MaxDurationMs { get; }

    public int MaxSchemaDepth { get; }

    public int MaxSchemaBytes { get; }

    private static int InRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name);
        }

        return value;
    }
}

public sealed class WorkflowStepReference
{
    public WorkflowStepReference(string kind, JsonElement? settings = null)
    {
        Kind = WorkflowValidation.RequiredIdentifier(
            kind,
            nameof(kind),
            128,
            allowSlash: true);
        Settings = settings.HasValue
            ? settings.Value.Clone()
            : WorkflowJson.CreateEmptyObject();
        if (Settings.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Step settings must be a JSON object.",
                nameof(settings));
        }
    }

    public string Kind { get; }

    public JsonElement Settings { get; }
}

public sealed class WorkflowForEachDefinition
{
    public WorkflowForEachDefinition(
        WorkflowStepReference body,
        string sourcePointer,
        string itemIdentityPointer,
        int maxItems,
        JsonElement itemInputSchema,
        JsonElement itemOutputSchema)
    {
        Body = body ?? throw new ArgumentNullException(nameof(body));
        SourcePointer = sourcePointer
            ?? throw new ArgumentNullException(nameof(sourcePointer));
        ItemIdentityPointer = itemIdentityPointer
            ?? throw new ArgumentNullException(nameof(itemIdentityPointer));
        if (maxItems < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxItems));
        }

        MaxItems = maxItems;
        ItemInputSchema = itemInputSchema.Clone();
        ItemOutputSchema = itemOutputSchema.Clone();
    }

    public WorkflowStepReference Body { get; }

    public string SourcePointer { get; }

    public string ItemIdentityPointer { get; }

    public int MaxItems { get; }

    public JsonElement ItemInputSchema { get; }

    public JsonElement ItemOutputSchema { get; }
}

public sealed class WorkflowReduceDefinition
{
    public WorkflowReduceDefinition(WorkflowStepReference reducer)
    {
        Reducer = reducer ?? throw new ArgumentNullException(nameof(reducer));
    }

    public WorkflowStepReference Reducer { get; }
}

public sealed class WorkflowLoopDefinition
{
    public WorkflowLoopDefinition(
        WorkflowStepReference body,
        string untilPointer,
        int maxIterations,
        JsonElement iterationInputSchema,
        JsonElement iterationOutputSchema)
    {
        Body = body ?? throw new ArgumentNullException(nameof(body));
        UntilPointer = untilPointer
            ?? throw new ArgumentNullException(nameof(untilPointer));
        if (maxIterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxIterations));
        }

        MaxIterations = maxIterations;
        IterationInputSchema = iterationInputSchema.Clone();
        IterationOutputSchema = iterationOutputSchema.Clone();
    }

    public WorkflowStepReference Body { get; }

    public string UntilPointer { get; }

    public int MaxIterations { get; }

    public JsonElement IterationInputSchema { get; }

    public JsonElement IterationOutputSchema { get; }
}

public sealed class WorkflowStageDefinition
{
    private WorkflowStageDefinition(
        string id,
        WorkflowStageKind kind,
        IEnumerable<string>? dependsOn,
        JsonElement inputSchema,
        JsonElement outputSchema,
        WorkflowStepReference? step,
        WorkflowForEachDefinition? forEach,
        WorkflowReduceDefinition? reduce,
        WorkflowLoopDefinition? loop)
    {
        Id = WorkflowValidation.RequiredIdentifier(
            id,
            nameof(id),
            128,
            allowSlash: false);
        Kind = kind;
        DependsOn = WorkflowCollections.MaterializeBounded(
            dependsOn ?? Array.Empty<string>(),
            4_096,
            nameof(dependsOn));
        InputSchema = inputSchema.Clone();
        OutputSchema = outputSchema.Clone();
        Step = step;
        ForEach = forEach;
        Reduce = reduce;
        Loop = loop;
    }

    public string Id { get; }

    public WorkflowStageKind Kind { get; }

    public IReadOnlyList<string> DependsOn { get; }

    public JsonElement InputSchema { get; }

    public JsonElement OutputSchema { get; }

    public WorkflowStepReference? Step { get; }

    public WorkflowForEachDefinition? ForEach { get; }

    public WorkflowReduceDefinition? Reduce { get; }

    public WorkflowLoopDefinition? Loop { get; }

    public static WorkflowStageDefinition CreateStep(
        string id,
        WorkflowStepReference step,
        JsonElement inputSchema,
        JsonElement outputSchema,
        IEnumerable<string>? dependsOn = null)
    {
        return new WorkflowStageDefinition(
            id,
            WorkflowStageKind.Step,
            dependsOn,
            inputSchema,
            outputSchema,
            step ?? throw new ArgumentNullException(nameof(step)),
            null,
            null,
            null);
    }

    public static WorkflowStageDefinition CreateForeach(
        string id,
        WorkflowForEachDefinition forEach,
        JsonElement inputSchema,
        JsonElement outputSchema,
        IEnumerable<string>? dependsOn = null)
    {
        return new WorkflowStageDefinition(
            id,
            WorkflowStageKind.Foreach,
            dependsOn,
            inputSchema,
            outputSchema,
            null,
            forEach ?? throw new ArgumentNullException(nameof(forEach)),
            null,
            null);
    }

    public static WorkflowStageDefinition CreateReduce(
        string id,
        WorkflowReduceDefinition reduce,
        JsonElement inputSchema,
        JsonElement outputSchema,
        IEnumerable<string>? dependsOn = null)
    {
        return new WorkflowStageDefinition(
            id,
            WorkflowStageKind.Reduce,
            dependsOn,
            inputSchema,
            outputSchema,
            null,
            null,
            reduce ?? throw new ArgumentNullException(nameof(reduce)),
            null);
    }

    public static WorkflowStageDefinition CreateLoop(
        string id,
        WorkflowLoopDefinition loop,
        JsonElement inputSchema,
        JsonElement outputSchema,
        IEnumerable<string>? dependsOn = null)
    {
        return new WorkflowStageDefinition(
            id,
            WorkflowStageKind.Loop,
            dependsOn,
            inputSchema,
            outputSchema,
            null,
            null,
            null,
            loop ?? throw new ArgumentNullException(nameof(loop)));
    }
}

public sealed class WorkflowDefinition
{
    public WorkflowDefinition(
        string id,
        string version,
        JsonElement inputSchema,
        JsonElement outputSchema,
        string outputStageId,
        IEnumerable<WorkflowStageDefinition> stages,
        WorkflowLimits? limits = null)
    {
        Id = WorkflowValidation.RequiredIdentifier(
            id,
            nameof(id),
            128,
            allowSlash: false);
        Version = WorkflowValidation.RequiredIdentifier(
            version,
            nameof(version),
            64,
            allowSlash: false);
        InputSchema = inputSchema.Clone();
        OutputSchema = outputSchema.Clone();
        OutputStageId = WorkflowValidation.RequiredIdentifier(
            outputStageId,
            nameof(outputStageId),
            128,
            allowSlash: false);
        Stages = WorkflowCollections.MaterializeBounded(
            stages ?? throw new ArgumentNullException(nameof(stages)),
            4_097,
            nameof(stages));
        Limits = limits ?? new WorkflowLimits();
    }

    public string Id { get; }

    public string Version { get; }

    public JsonElement InputSchema { get; }

    public JsonElement OutputSchema { get; }

    public string OutputStageId { get; }

    public IReadOnlyList<WorkflowStageDefinition> Stages { get; }

    public WorkflowLimits Limits { get; }
}

public sealed class WorkflowDiagnostic
{
    public WorkflowDiagnostic(string code, string message, string? stageId = null)
    {
        Code = code;
        Message = message;
        StageId = stageId;
    }

    public string Code { get; }

    public string Message { get; }

    public string? StageId { get; }
}

public sealed class WorkflowCompilationException : Exception
{
    public WorkflowCompilationException(
        IReadOnlyList<WorkflowDiagnostic> diagnostics)
        : base(
            diagnostics is null || diagnostics.Count == 0
                ? "Workflow compilation failed."
                : diagnostics[0].Message)
    {
        Diagnostics = diagnostics
            ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public IReadOnlyList<WorkflowDiagnostic> Diagnostics { get; }
}

public sealed class CompiledWorkflowStage
{
    internal CompiledWorkflowStage(
        WorkflowStageDefinition definition,
        int ordinal,
        IReadOnlyList<string> dependencyOrder)
    {
        Definition = definition;
        Ordinal = ordinal;
        DependencyOrder = dependencyOrder;
    }

    public WorkflowStageDefinition Definition { get; }

    public int Ordinal { get; }

    public IReadOnlyList<string> DependencyOrder { get; }
}

public sealed class CompiledWorkflow
{
    internal CompiledWorkflow(
        WorkflowDefinition definition,
        string definitionDigest,
        IReadOnlyList<CompiledWorkflowStage> stages)
    {
        Definition = definition;
        DefinitionDigest = definitionDigest;
        Stages = stages;
        StagesById = new ReadOnlyDictionary<string, CompiledWorkflowStage>(
            stages.ToDictionary(
                item => item.Definition.Id,
                StringComparer.Ordinal));
    }

    public WorkflowDefinition Definition { get; }

    public string DefinitionDigest { get; }

    public IReadOnlyList<CompiledWorkflowStage> Stages { get; }

    public IReadOnlyDictionary<string, CompiledWorkflowStage> StagesById { get; }
}

internal static class WorkflowCollections
{
    public static IReadOnlyList<T> MaterializeBounded<T>(
        IEnumerable<T> source,
        int maximum,
        string parameterName)
    {
        if (source is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        var result = new List<T>();
        using var enumerator = source.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (result.Count >= maximum)
            {
                throw new ArgumentException(
                    $"The sequence exceeds the hard limit of {maximum} items.",
                    parameterName);
            }

            result.Add(enumerator.Current);
        }

        return new ReadOnlyCollection<T>(result);
    }
}

internal static class WorkflowValidation
{
    public static string RequiredIdentifier(
        string? value,
        string parameterName,
        int maximumLength,
        bool allowSlash)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || !IsAsciiIdentifier(value, allowSlash))
        {
            throw new ArgumentException(
                "The value must be a bounded ASCII identifier.",
                parameterName);
        }

        return value;
    }

    private static bool IsAsciiIdentifier(string value, bool allowSlash)
    {
        foreach (var character in value)
        {
            if ((character >= 'a' && character <= 'z')
                || (character >= 'A' && character <= 'Z')
                || (character >= '0' && character <= '9')
                || character is '.' or '_' or '-' or ':'
                || (allowSlash && character == '/'))
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
