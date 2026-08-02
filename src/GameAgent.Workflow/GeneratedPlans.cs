using System.Buffers;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Workflow;

public sealed class GeneratedPlanCommandDescriptor
{
    public GeneratedPlanCommandDescriptor(
        string command,
        JsonElement argumentsSchema,
        JsonElement resultSchema)
        : this(command, argumentsSchema, resultSchema, null)
    {
    }

    public GeneratedPlanCommandDescriptor(
        string command,
        JsonElement argumentsSchema,
        JsonElement resultSchema,
        JsonElement? executionInputSchema)
    {
        Command = WorkflowValidation.RequiredIdentifier(
            command,
            nameof(command),
            128,
            allowSlash: true);
        ArgumentsSchema = argumentsSchema.Clone();
        ResultSchema = resultSchema.Clone();
        ExecutionInputSchema = executionInputSchema?.Clone();
    }

    public string Command { get; }

    public JsonElement ArgumentsSchema { get; }

    public JsonElement ResultSchema { get; }

    /// <summary>
    /// Host-owned schema for the changing input supplied to a generated
    /// foreach body or loop body. Fixed model-produced arguments remain
    /// governed by <see cref="ArgumentsSchema"/>.
    /// </summary>
    public JsonElement? ExecutionInputSchema { get; }
}

public sealed class GeneratedPlanAdmissionOptions
{
    public int MaxPlanUtf8Bytes { get; set; } = 512 * 1024;

    public int MaxSteps { get; set; } = 512;

    public int MaxDependenciesPerStep { get; set; } = 32;

    public int MaxForeachItems { get; set; } = 256;

    public int MaxLoopIterations { get; set; } = 64;

    public int MaxExpandedStageExecutions { get; set; } = 4_096;

    public double MaxDurationSecondsPerStep { get; set; } = 86_400;

    internal void Validate()
    {
        if (MaxPlanUtf8Bytes is < 1_024 or > 16 * 1024 * 1024
            || MaxSteps is < 1 or > 4_096
            || MaxDependenciesPerStep is < 0 or > 1_024
            || MaxForeachItems is < 1 or > 16_384
            || MaxLoopIterations is < 1 or > 16_384
            || MaxExpandedStageExecutions is < 1 or > 1_000_000
            || double.IsNaN(MaxDurationSecondsPerStep)
            || double.IsInfinity(MaxDurationSecondsPerStep)
            || MaxDurationSecondsPerStep <= 0
            || MaxDurationSecondsPerStep > 604_800)
        {
            throw new ArgumentOutOfRangeException(
                nameof(GeneratedPlanAdmissionOptions));
        }
    }
}

public sealed class GeneratedPlanAdmissionException : Exception
{
    public GeneratedPlanAdmissionException(
        string reasonCode,
        string message,
        string? stepId = null)
        : base(message)
    {
        ReasonCode = reasonCode;
        StepId = stepId;
    }

    public string ReasonCode { get; }

    public string? StepId { get; }
}

/// <summary>
/// Admits a model-produced command graph into the durable workflow runtime.
/// Command schemas and executors come only from the game; generated content
/// cannot register executable code or new command kinds.
/// </summary>
public sealed class GeneratedPlanCompiler
{
    private static readonly HashSet<string> RootProperties = new(
        new[] { "planId", "version", "outputStepId", "steps" },
        StringComparer.Ordinal);
    private static readonly HashSet<string> CommandStepProperties = new(
        new[]
        {
            "id", "kind", "command", "arguments", "dependsOn",
            "durationSeconds"
        },
        StringComparer.Ordinal);
    private static readonly HashSet<string> ForeachStepProperties = new(
        new[]
        {
            "id", "kind", "command", "arguments", "dependsOn",
            "durationSeconds", "sourcePointer", "itemIdentityPointer",
            "maxItems"
        },
        StringComparer.Ordinal);
    private static readonly HashSet<string> ReduceStepProperties = new(
        CommandStepProperties,
        StringComparer.Ordinal);
    private static readonly HashSet<string> LoopStepProperties = new(
        new[]
        {
            "id", "kind", "command", "arguments", "dependsOn",
            "durationSeconds", "untilPointer", "maxIterations"
        },
        StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, GeneratedPlanCommandDescriptor>
        _commands;
    private readonly ToolArgumentValidator _validator;
    private readonly GeneratedPlanAdmissionOptions _options;

    public GeneratedPlanCompiler(
        IEnumerable<GeneratedPlanCommandDescriptor> commands,
        GeneratedPlanAdmissionOptions? options = null,
        ToolArgumentValidator? validator = null)
    {
        if (commands is null)
        {
            throw new ArgumentNullException(nameof(commands));
        }

        _options = options ?? new GeneratedPlanAdmissionOptions();
        _options.Validate();
        _validator = validator ?? new ToolArgumentValidator();
        var catalog = new Dictionary<string, GeneratedPlanCommandDescriptor>(
            StringComparer.Ordinal);
        foreach (var descriptor in commands.Take(1_025))
        {
            if (descriptor is null
                || catalog.Count >= 1_024
                || !catalog.TryAdd(descriptor.Command, descriptor))
            {
                throw new ArgumentException(
                    "Generated plan command catalog is null, duplicated, or too large.",
                    nameof(commands));
            }

            EnsureSchema(descriptor.ArgumentsSchema, "arguments schema");
            EnsureSchema(descriptor.ResultSchema, "result schema");
            if (descriptor.ExecutionInputSchema.HasValue)
            {
                EnsureSchema(
                    descriptor.ExecutionInputSchema.Value,
                    "execution input schema");
            }
        }

        if (catalog.Count == 0)
        {
            throw new ArgumentException(
                "At least one generated plan command is required.",
                nameof(commands));
        }

        _commands = new ReadOnlyDictionary<string, GeneratedPlanCommandDescriptor>(
            catalog);
    }

    public CompiledWorkflow Compile(JsonElement generatedPlan)
    {
        if (generatedPlan.ValueKind != JsonValueKind.Object
            || Encoding.UTF8.GetByteCount(generatedPlan.GetRawText())
               > _options.MaxPlanUtf8Bytes)
        {
            throw Invalid(
                "generated_plan_invalid",
                "Generated plan must be a bounded JSON object.");
        }

        RejectUnknownProperties(generatedPlan, RootProperties, null);
        var planId = RequiredIdentifier(generatedPlan, "planId", 128, null);
        var version = RequiredIdentifier(generatedPlan, "version", 64, null);
        var outputStepId = RequiredIdentifier(
            generatedPlan,
            "outputStepId",
            128,
            null);
        if (!generatedPlan.TryGetProperty("steps", out var steps)
            || steps.ValueKind != JsonValueKind.Array
            || steps.GetArrayLength() is < 1
            || steps.GetArrayLength() > _options.MaxSteps)
        {
            throw Invalid(
                "generated_plan_step_limit",
                "Generated plan contains no steps or exceeds its step limit.");
        }

        var admittedSteps = new List<AdmittedStep>(steps.GetArrayLength());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long maximumExecutions = 0;
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object)
            {
                throw Invalid(
                    "generated_plan_step_invalid",
                    "Every generated plan step must be an object.");
            }

            var kind = ReadKind(step);
            RejectUnknownProperties(step, PropertiesFor(kind), null);
            var stepId = RequiredIdentifier(step, "id", 128, null);
            if (!ids.Add(stepId))
            {
                throw Invalid(
                    "generated_plan_step_duplicate",
                    $"Generated plan step '{stepId}' is duplicated.",
                    stepId);
            }

            var command = RequiredIdentifier(step, "command", 128, stepId);
            if (!_commands.TryGetValue(command, out var descriptor))
            {
                throw Invalid(
                    "generated_plan_command_not_admitted",
                    $"Generated plan command '{command}' is not admitted by the game.",
                    stepId);
            }

            if (!step.TryGetProperty("arguments", out var arguments)
                || arguments.ValueKind == JsonValueKind.Undefined)
            {
                throw Invalid(
                    "generated_plan_arguments_missing",
                    $"Generated plan step '{stepId}' has no arguments.",
                    stepId);
            }

            var validation = _validator.Validate(
                descriptor.ArgumentsSchema,
                arguments);
            if (!validation.IsValid)
            {
                throw Invalid(
                    "generated_plan_arguments_invalid",
                    $"Generated plan step '{stepId}' arguments do not match the host schema.",
                    stepId);
            }

            var dependencies = ReadDependencies(step, stepId);
            var duration = ReadDuration(step, stepId);
            var composite = ReadCompositeSettings(
                step,
                stepId,
                kind,
                descriptor);
            maximumExecutions = checked(
                maximumExecutions + composite.MaximumExecutions);
            if (maximumExecutions > _options.MaxExpandedStageExecutions)
            {
                throw Invalid(
                    "generated_plan_execution_limit",
                    "Generated plan exceeds its expanded execution limit.",
                    stepId);
            }

            admittedSteps.Add(new AdmittedStep(
                stepId,
                kind,
                command,
                arguments.Clone(),
                dependencies,
                duration,
                descriptor,
                composite));
        }

        var byId = admittedSteps.ToDictionary(item => item.StepId, StringComparer.Ordinal);
        var definitions = new List<WorkflowStageDefinition>(admittedSteps.Count);
        foreach (var admitted in admittedSteps)
        {
            foreach (var dependency in admitted.Dependencies)
            {
                if (!byId.ContainsKey(dependency))
                {
                    throw Invalid(
                        "generated_plan_dependency_invalid",
                        $"Generated plan step '{admitted.StepId}' depends on missing step '{dependency}'.",
                        admitted.StepId);
                }
            }

            var settings = CreateSettings(
                planId,
                admitted.StepId,
                admitted.Command,
                admitted.Arguments,
                admitted.Duration);
            var stepReference = new WorkflowStepReference(
                GeneratedPlanStepExecutor.ExecutorKind,
                settings);
            definitions.Add(CreateStageDefinition(
                admitted,
                stepReference,
                byId));
        }

        var limits = new WorkflowLimits(
            maxStages: _options.MaxSteps,
            maxDependenciesPerStage: _options.MaxDependenciesPerStep,
            maxParallelism: Math.Min(64, _options.MaxSteps),
            maxForeachItems: _options.MaxForeachItems,
            maxLoopIterations: _options.MaxLoopIterations,
            maxStageExecutions: _options.MaxExpandedStageExecutions,
            maxInputBytes: Math.Min(_options.MaxPlanUtf8Bytes, 262_144),
            maxStageOutputBytes: 262_144,
            maxRetainedOutputBytes: (int)Math.Min(
                67_108_864L,
                Math.Max(262_144L, (long)_options.MaxSteps * 262_144L)),
            maxDurationMs: 86_400_000);
        try
        {
            return new WorkflowCompiler().Compile(
                new WorkflowDefinition(
                    planId,
                    version,
                    EmptySchema(),
                    ReadOutputSchema(admittedSteps, outputStepId),
                    outputStepId,
                    definitions,
                    limits));
        }
        catch (WorkflowCompilationException exception)
        {
            throw new GeneratedPlanAdmissionException(
                "generated_plan_graph_invalid",
                exception.Message,
                exception.Diagnostics.FirstOrDefault()?.StageId);
        }
    }

    private WorkflowStageDefinition CreateStageDefinition(
        AdmittedStep admitted,
        WorkflowStepReference stepReference,
        IReadOnlyDictionary<string, AdmittedStep> steps)
    {
        var inputSchema = admitted.Kind == GeneratedPlanStageKind.Reduce
            ? CreateReduceInputSchema(admitted.Dependencies, steps)
            : CreateInputSchema(admitted.Dependencies, steps);
        return admitted.Kind switch
        {
            GeneratedPlanStageKind.Command =>
                WorkflowStageDefinition.CreateStep(
                    admitted.StepId,
                    stepReference,
                    inputSchema,
                    admitted.Descriptor.ResultSchema,
                    admitted.Dependencies),
            GeneratedPlanStageKind.Foreach =>
                WorkflowStageDefinition.CreateForeach(
                    admitted.StepId,
                    new WorkflowForEachDefinition(
                        stepReference,
                        admitted.Composite.SourcePointer!,
                        admitted.Composite.ItemIdentityPointer!,
                        admitted.Composite.MaximumExecutions,
                        admitted.Descriptor.ExecutionInputSchema!.Value,
                        admitted.Descriptor.ResultSchema),
                    inputSchema,
                    CreateArraySchema(
                        admitted.Descriptor.ResultSchema,
                        admitted.Composite.MaximumExecutions),
                    admitted.Dependencies),
            GeneratedPlanStageKind.Reduce =>
                WorkflowStageDefinition.CreateReduce(
                    admitted.StepId,
                    new WorkflowReduceDefinition(stepReference),
                    inputSchema,
                    admitted.Descriptor.ResultSchema,
                    admitted.Dependencies),
            GeneratedPlanStageKind.Loop =>
                WorkflowStageDefinition.CreateLoop(
                    admitted.StepId,
                    new WorkflowLoopDefinition(
                        stepReference,
                        admitted.Composite.UntilPointer!,
                        admitted.Composite.MaximumExecutions,
                        admitted.Descriptor.ExecutionInputSchema!.Value,
                        admitted.Descriptor.ResultSchema),
                    inputSchema,
                    admitted.Descriptor.ResultSchema,
                    admitted.Dependencies),
            _ => throw Invalid(
                "generated_plan_kind_invalid",
                "Generated plan stage kind is invalid.",
                admitted.StepId)
        };
    }

    private CompositeSettings ReadCompositeSettings(
        JsonElement step,
        string stepId,
        GeneratedPlanStageKind kind,
        GeneratedPlanCommandDescriptor descriptor)
    {
        if (kind is GeneratedPlanStageKind.Foreach
            or GeneratedPlanStageKind.Loop
            && !descriptor.ExecutionInputSchema.HasValue)
        {
            throw Invalid(
                "generated_plan_execution_schema_missing",
                $"Generated plan stage '{stepId}' requires a host-owned execution input schema.",
                stepId);
        }

        return kind switch
        {
            GeneratedPlanStageKind.Command => new CompositeSettings(1),
            GeneratedPlanStageKind.Reduce => new CompositeSettings(1),
            GeneratedPlanStageKind.Foreach => new CompositeSettings(
                ReadBoundedPositiveInt(
                    step,
                    "maxItems",
                    _options.MaxForeachItems,
                    stepId),
                sourcePointer: ReadPointer(step, "sourcePointer", stepId),
                itemIdentityPointer: ReadPointer(
                    step,
                    "itemIdentityPointer",
                    stepId)),
            GeneratedPlanStageKind.Loop => new CompositeSettings(
                ReadBoundedPositiveInt(
                    step,
                    "maxIterations",
                    _options.MaxLoopIterations,
                    stepId),
                untilPointer: ReadPointer(step, "untilPointer", stepId)),
            _ => throw Invalid(
                "generated_plan_kind_invalid",
                "Generated plan stage kind is invalid.",
                stepId)
        };
    }

    private static GeneratedPlanStageKind ReadKind(JsonElement step)
    {
        if (!step.TryGetProperty("kind", out var value))
        {
            return GeneratedPlanStageKind.Command;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid(
                "generated_plan_kind_invalid",
                "Generated plan stage kind must be a string.");
        }

        return value.GetString() switch
        {
            "command" => GeneratedPlanStageKind.Command,
            "foreach" => GeneratedPlanStageKind.Foreach,
            "reduce" => GeneratedPlanStageKind.Reduce,
            "loop" => GeneratedPlanStageKind.Loop,
            _ => throw Invalid(
                "generated_plan_kind_invalid",
                "Generated plan stage kind is not admitted.")
        };
    }

    private static ISet<string> PropertiesFor(GeneratedPlanStageKind kind) =>
        kind switch
        {
            GeneratedPlanStageKind.Command => CommandStepProperties,
            GeneratedPlanStageKind.Foreach => ForeachStepProperties,
            GeneratedPlanStageKind.Reduce => ReduceStepProperties,
            GeneratedPlanStageKind.Loop => LoopStepProperties,
            _ => CommandStepProperties
        };

    private static string ReadPointer(
        JsonElement step,
        string propertyName,
        string stepId)
    {
        if (!step.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || !WorkflowJson.IsValidPointer(value.GetString()!))
        {
            throw Invalid(
                "generated_plan_pointer_invalid",
                $"Generated plan stage '{stepId}' has an invalid '{propertyName}'.",
                stepId);
        }

        return value.GetString()!;
    }

    private static int ReadBoundedPositiveInt(
        JsonElement step,
        string propertyName,
        int maximum,
        string stepId)
    {
        if (!step.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result)
            || result < 1
            || result > maximum)
        {
            throw Invalid(
                "generated_plan_composite_limit_invalid",
                $"Generated plan stage '{stepId}' has an invalid '{propertyName}'.",
                stepId);
        }

        return result;
    }

    private IReadOnlyList<string> ReadDependencies(
        JsonElement step,
        string stepId)
    {
        if (!step.TryGetProperty("dependsOn", out var dependencies))
        {
            return Array.Empty<string>();
        }

        if (dependencies.ValueKind != JsonValueKind.Array
            || dependencies.GetArrayLength() > _options.MaxDependenciesPerStep)
        {
            throw Invalid(
                "generated_plan_dependency_limit",
                $"Generated plan step '{stepId}' has invalid dependencies.",
                stepId);
        }

        var result = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in dependencies.EnumerateArray())
        {
            if (dependency.ValueKind != JsonValueKind.String)
            {
                throw Invalid(
                    "generated_plan_dependency_invalid",
                    $"Generated plan step '{stepId}' has a non-string dependency.",
                    stepId);
            }

            var value = dependency.GetString()!;
            WorkflowValidation.RequiredIdentifier(
                value,
                nameof(dependencies),
                128,
                allowSlash: false);
            if (!unique.Add(value))
            {
                throw Invalid(
                    "generated_plan_dependency_duplicate",
                    $"Generated plan step '{stepId}' repeats a dependency.",
                    stepId);
            }

            result.Add(value);
        }

        return result;
    }

    private double? ReadDuration(JsonElement step, string stepId)
    {
        if (!step.TryGetProperty("durationSeconds", out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var duration)
            || double.IsNaN(duration)
            || double.IsInfinity(duration)
            || duration < 0
            || duration > _options.MaxDurationSecondsPerStep)
        {
            throw Invalid(
                "generated_plan_duration_invalid",
                $"Generated plan step '{stepId}' has an invalid floating-point duration.",
                stepId);
        }

        return duration;
    }

    private static JsonElement ReadOutputSchema(
        IReadOnlyList<AdmittedStep> steps,
        string outputStepId)
    {
        foreach (var step in steps)
        {
            if (step.StepId == outputStepId)
            {
                return step.Kind == GeneratedPlanStageKind.Foreach
                    ? CreateArraySchema(
                        step.Descriptor.ResultSchema,
                        step.Composite.MaximumExecutions)
                    : step.Descriptor.ResultSchema.Clone();
            }
        }

        throw Invalid(
            "generated_plan_output_step_invalid",
            "Generated plan output step does not exist.");
    }

    private static JsonElement CreateSettings(
        string planId,
        string stepId,
        string command,
        JsonElement arguments,
        double? duration)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("planId", planId);
            writer.WriteString("stepId", stepId);
            writer.WriteString("command", command);
            writer.WritePropertyName("arguments");
            arguments.WriteTo(writer);
            if (duration.HasValue)
            {
                writer.WriteNumber("durationSeconds", duration.Value);
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    private static void RejectUnknownProperties(
        JsonElement value,
        ISet<string> allowed,
        string? stepId)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw Invalid(
                    "generated_plan_property_unknown",
                    $"Generated plan contains unknown property '{property.Name}'.",
                    stepId);
            }
        }
    }

    private static string RequiredIdentifier(
        JsonElement parent,
        string name,
        int maximumLength,
        string? stepId)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw Invalid(
                "generated_plan_property_missing",
                $"Generated plan property '{name}' is required.",
                stepId);
        }

        try
        {
            return WorkflowValidation.RequiredIdentifier(
                value.GetString()!,
                name,
                maximumLength,
                allowSlash: name == "command");
        }
        catch (ArgumentException exception)
        {
            throw new GeneratedPlanAdmissionException(
                "generated_plan_identifier_invalid",
                exception.Message,
                stepId);
        }
    }

    private void EnsureSchema(JsonElement schema, string label)
    {
        var validation = _validator.Validate(schema, EmptyObject());
        if (validation.Errors.Any(error => error.Code.StartsWith(
                "schema_",
                StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Generated plan {label} is invalid.");
        }
    }

    private static JsonElement CreateInputSchema(
        IReadOnlyList<string> dependencies,
        IReadOnlyDictionary<string, AdmittedStep> steps)
    {
        if (dependencies.Count == 0)
        {
            return EmptySchema();
        }

        if (dependencies.Count == 1)
        {
            return OutputSchemaFor(steps[dependencies[0]]);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            foreach (var dependency in dependencies)
            {
                writer.WritePropertyName(dependency);
                OutputSchemaFor(steps[dependency]).WriteTo(writer);
            }

            writer.WriteEndObject();
            writer.WriteStartArray("required");
            foreach (var dependency in dependencies)
            {
                writer.WriteStringValue(dependency);
            }

            writer.WriteEndArray();
            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    private static JsonElement CreateReduceInputSchema(
        IReadOnlyList<string> dependencies,
        IReadOnlyDictionary<string, AdmittedStep> steps)
    {
        if (dependencies.Count == 0)
        {
            throw Invalid(
                "generated_plan_reduce_dependency_missing",
                "A generated reduce stage requires at least one dependency.");
        }

        var outputSchemas = dependencies
            .Select(dependency => OutputSchemaFor(steps[dependency]))
            .ToArray();
        var commonOutput = outputSchemas[0];
        var commonDigest = CanonicalJsonDigest.ComputeSha256(commonOutput);
        if (outputSchemas.Skip(1).Any(schema =>
                !string.Equals(
                    CanonicalJsonDigest.ComputeSha256(schema),
                    commonDigest,
                    StringComparison.Ordinal)))
        {
            throw Invalid(
                "generated_plan_reduce_schema_mismatch",
                "Generated reduce dependencies must have the same output schema.");
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "array");
            writer.WriteNumber("maxItems", dependencies.Count);
            writer.WritePropertyName("items");
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            writer.WritePropertyName("stageId");
            WriteBoundedStringSchema(writer, 128);
            writer.WritePropertyName("instanceId");
            WriteBoundedStringSchema(writer, 512);
            writer.WritePropertyName("output");
            commonOutput.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteStartArray("required");
            writer.WriteStringValue("stageId");
            writer.WriteStringValue("instanceId");
            writer.WriteStringValue("output");
            writer.WriteEndArray();
            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    private static JsonElement CreateArraySchema(
        JsonElement itemSchema,
        int maximumItems)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "array");
            writer.WriteNumber("maxItems", maximumItems);
            writer.WritePropertyName("items");
            itemSchema.WriteTo(writer);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    private static JsonElement OutputSchemaFor(AdmittedStep step) =>
        step.Kind == GeneratedPlanStageKind.Foreach
            ? CreateArraySchema(
                step.Descriptor.ResultSchema,
                step.Composite.MaximumExecutions)
            : step.Descriptor.ResultSchema.Clone();

    private static void WriteBoundedStringSchema(
        Utf8JsonWriter writer,
        int maximumLength)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "string");
        writer.WriteNumber("maxLength", maximumLength);
        writer.WriteEndObject();
    }

    private static JsonElement EmptySchema() =>
        JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")
            .RootElement.Clone();

    private static JsonElement EmptyObject() =>
        JsonDocument.Parse("{}").RootElement.Clone();

    private static GeneratedPlanAdmissionException Invalid(
        string code,
        string message,
        string? stepId = null) =>
        new(code, message, stepId);

    private sealed class AdmittedStep
    {
        public AdmittedStep(
            string stepId,
            GeneratedPlanStageKind kind,
            string command,
            JsonElement arguments,
            IReadOnlyList<string> dependencies,
            double? duration,
            GeneratedPlanCommandDescriptor descriptor,
            CompositeSettings composite)
        {
            StepId = stepId;
            Kind = kind;
            Command = command;
            Arguments = arguments;
            Dependencies = dependencies;
            Duration = duration;
            Descriptor = descriptor;
            Composite = composite;
        }

        public string StepId { get; }

        public GeneratedPlanStageKind Kind { get; }

        public string Command { get; }

        public JsonElement Arguments { get; }

        public IReadOnlyList<string> Dependencies { get; }

        public double? Duration { get; }

        public GeneratedPlanCommandDescriptor Descriptor { get; }

        public CompositeSettings Composite { get; }
    }

    private enum GeneratedPlanStageKind
    {
        Command,
        Foreach,
        Reduce,
        Loop
    }

    private sealed class CompositeSettings
    {
        public CompositeSettings(
            int maximumExecutions,
            string? sourcePointer = null,
            string? itemIdentityPointer = null,
            string? untilPointer = null)
        {
            MaximumExecutions = maximumExecutions;
            SourcePointer = sourcePointer;
            ItemIdentityPointer = itemIdentityPointer;
            UntilPointer = untilPointer;
        }

        public int MaximumExecutions { get; }

        public string? SourcePointer { get; }

        public string? ItemIdentityPointer { get; }

        public string? UntilPointer { get; }
    }
}

public sealed class GeneratedPlanCommandRequest
{
    public string ExecutionId { get; set; } = string.Empty;

    public string PlanId { get; set; } = string.Empty;

    public string StepId { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public JsonElement Arguments { get; set; }

    public JsonElement UpstreamInput { get; set; }

    public double? DurationSeconds { get; set; }
}

public sealed class GeneratedPlanCommandReceipt
{
    public bool Succeeded { get; set; }

    public JsonElement? Result { get; set; }

    public string? ReasonCode { get; set; }
}

public interface IGeneratedPlanCommandHost
{
    ValueTask<GeneratedPlanCommandReceipt> ExecuteAsync(
        GeneratedPlanCommandRequest request,
        CancellationToken cancellationToken);

    ValueTask<GeneratedPlanCommandReceipt?> TryGetReceiptAsync(
        string executionId,
        CancellationToken cancellationToken);
}

public sealed class GeneratedPlanStepExecutor : IWorkflowStepExecutor
{
    public const string ExecutorKind = "generated_plan/command";
    private readonly IGeneratedPlanCommandHost _host;

    public GeneratedPlanStepExecutor(IGeneratedPlanCommandHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public string Kind => ExecutorKind;

    public ValueTask<WorkflowStepResult> ExecuteAsync(
        WorkflowStepContext context,
        JsonElement input,
        CancellationToken cancellationToken) =>
        ExecuteCoreAsync(context, input, recover: false, cancellationToken);

    public ValueTask<WorkflowStepResult> RecoverAsync(
        WorkflowStepContext context,
        JsonElement input,
        CancellationToken cancellationToken) =>
        ExecuteCoreAsync(context, input, recover: true, cancellationToken);

    private async ValueTask<WorkflowStepResult> ExecuteCoreAsync(
        WorkflowStepContext context,
        JsonElement input,
        bool recover,
        CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        // Instance IDs are already deterministic and globally bound to the
        // workflow run. Reusing that bounded identity keeps receipt,
        // idempotency, and external-attention keys below common 128-byte
        // durable identifier limits.
        var executionId = context.InstanceId;
        if (recover)
        {
            var existing = await _host
                .TryGetReceiptAsync(executionId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return ToResult(existing);
            }
        }

        var prepared = JsonArrayBuilder.Object(
            ("executionId", JsonArrayBuilder.String(executionId)),
            ("state", JsonArrayBuilder.String("prepared")));
        if (!await context.SaveCheckpointAsync(prepared, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new WorkflowExecutorInterruptedException(
                "Generated plan command checkpoint was rejected.");
        }

        var receipt = await _host.ExecuteAsync(
                new GeneratedPlanCommandRequest
                {
                    ExecutionId = executionId,
                    PlanId = settings.GetProperty("planId").GetString()!,
                    StepId = settings.GetProperty("stepId").GetString()!,
                    Command = settings.GetProperty("command").GetString()!,
                    Arguments = settings.GetProperty("arguments").Clone(),
                    UpstreamInput = input.Clone(),
                    DurationSeconds = settings.TryGetProperty(
                        "durationSeconds",
                        out var duration)
                        ? duration.GetDouble()
                        : null
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (receipt is null)
        {
            throw new WorkflowExecutorInterruptedException(
                "Generated plan command host returned no receipt.");
        }

        return ToResult(receipt);
    }

    private static WorkflowStepResult ToResult(
        GeneratedPlanCommandReceipt receipt)
    {
        if (!receipt.Succeeded)
        {
            return WorkflowStepResult.Failed(
                string.IsNullOrWhiteSpace(receipt.ReasonCode)
                    ? "generated_plan_command_failed"
                    : receipt.ReasonCode);
        }

        return WorkflowStepResult.Completed(
            receipt.Result?.Clone() ?? JsonArrayBuilder.Null());
    }
}
