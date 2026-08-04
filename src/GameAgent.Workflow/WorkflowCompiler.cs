using System.Collections.ObjectModel;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Workflow;

public sealed class WorkflowCompiler
{
    public CompiledWorkflow Compile(WorkflowDefinition definition)
    {
        if (definition is null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        var diagnostics = new List<WorkflowDiagnostic>();
        var limits = definition.Limits;
        if (definition.Stages.Count == 0
            || definition.Stages.Count > limits.MaxStages)
        {
            diagnostics.Add(new WorkflowDiagnostic(
                WorkflowReasonCodes.LimitExceeded,
                "The workflow stage count is outside its declared limit."));
        }

        AddSchemaDiagnostic(
            diagnostics,
            WorkflowSchema.ValidateDefinition(
                definition.InputSchema,
                limits,
                "workflow input schema",
                null));
        AddSchemaDiagnostic(
            diagnostics,
            WorkflowSchema.ValidateDefinition(
                definition.OutputSchema,
                limits,
                "workflow output schema",
                null));

        var stagesById = new Dictionary<string, WorkflowStageDefinition>(
            StringComparer.Ordinal);
        foreach (var stage in definition.Stages)
        {
            if (stage is null)
            {
                diagnostics.Add(new WorkflowDiagnostic(
                    WorkflowReasonCodes.DefinitionInvalid,
                    "A workflow stage cannot be null."));
                continue;
            }

            if (!stagesById.TryAdd(stage.Id, stage))
            {
                diagnostics.Add(new WorkflowDiagnostic(
                    WorkflowReasonCodes.DuplicateStageId,
                    $"Stage '{stage.Id}' is declared more than once.",
                    stage.Id));
            }
        }

        foreach (var stage in stagesById.Values)
        {
            ValidateStage(stage, limits, diagnostics);
            ValidateDependencies(stage, stagesById, limits, diagnostics);
        }

        var topologicalIds = TopologicalSort(stagesById, diagnostics);
        ValidateOutputStage(
            definition.OutputStageId,
            stagesById,
            topologicalIds,
            diagnostics);

        if (diagnostics.Count > 0)
        {
            throw new WorkflowCompilationException(
                new ReadOnlyCollection<WorkflowDiagnostic>(diagnostics));
        }

        var ordinals = topologicalIds
            .Select((id, ordinal) => (id, ordinal))
            .ToDictionary(item => item.id, item => item.ordinal, StringComparer.Ordinal);
        var compiledStages = topologicalIds
            .Select((id, ordinal) =>
            {
                var stage = stagesById[id];
                var dependencyOrder = stage.DependsOn
                    .OrderBy(dependency => ordinals[dependency])
                    .ThenBy(dependency => dependency, StringComparer.Ordinal)
                    .ToArray();
                return new CompiledWorkflowStage(
                    stage,
                    ordinal,
                    new ReadOnlyCollection<string>(dependencyOrder));
            })
            .ToArray();
        var digest = ComputeDefinitionDigest(definition);
        return new CompiledWorkflow(
            definition,
            digest,
            new ReadOnlyCollection<CompiledWorkflowStage>(compiledStages));
    }

    private static void ValidateStage(
        WorkflowStageDefinition stage,
        WorkflowLimits limits,
        List<WorkflowDiagnostic> diagnostics)
    {
        AddSchemaDiagnostic(
            diagnostics,
            WorkflowSchema.ValidateDefinition(
                stage.InputSchema,
                limits,
                $"stage '{stage.Id}' input schema",
                stage.Id));
        AddSchemaDiagnostic(
            diagnostics,
            WorkflowSchema.ValidateDefinition(
                stage.OutputSchema,
                limits,
                $"stage '{stage.Id}' output schema",
                stage.Id));

        switch (stage.Kind)
        {
            case WorkflowStageKind.Step:
                if (stage.Step is null
                    || stage.ForEach is not null
                    || stage.Reduce is not null
                    || stage.Loop is not null)
                {
                    ShapeError(stage, diagnostics);
                    return;
                }

                ValidateStep(stage.Step, stage.Id, limits, diagnostics);
                break;
            case WorkflowStageKind.Foreach:
                if (stage.Step is not null
                    || stage.ForEach is null
                    || stage.Reduce is not null
                    || stage.Loop is not null)
                {
                    ShapeError(stage, diagnostics);
                    return;
                }

                var forEach = stage.ForEach;
                if (forEach.MaxItems > limits.MaxForeachItems
                    || !WorkflowJson.IsValidPointer(forEach.SourcePointer)
                    || !WorkflowJson.IsValidPointer(
                        forEach.ItemIdentityPointer))
                {
                    diagnostics.Add(new WorkflowDiagnostic(
                        WorkflowReasonCodes.StageShapeInvalid,
                        $"Foreach stage '{stage.Id}' has invalid bounds or JSON pointers.",
                        stage.Id));
                }

                ValidateStep(forEach.Body, stage.Id, limits, diagnostics);
                AddSchemaDiagnostic(
                    diagnostics,
                    WorkflowSchema.ValidateDefinition(
                        forEach.ItemInputSchema,
                        limits,
                        $"foreach stage '{stage.Id}' item input schema",
                        stage.Id));
                AddSchemaDiagnostic(
                    diagnostics,
                    WorkflowSchema.ValidateDefinition(
                        forEach.ItemOutputSchema,
                        limits,
                        $"foreach stage '{stage.Id}' item output schema",
                        stage.Id));
                break;
            case WorkflowStageKind.Reduce:
                if (stage.Step is not null
                    || stage.ForEach is not null
                    || stage.Reduce is null
                    || stage.Loop is not null)
                {
                    ShapeError(stage, diagnostics);
                    return;
                }

                if (stage.DependsOn.Count == 0)
                {
                    diagnostics.Add(new WorkflowDiagnostic(
                        WorkflowReasonCodes.StageShapeInvalid,
                        $"Reduce stage '{stage.Id}' requires at least one dependency.",
                        stage.Id));
                }

                ValidateStep(stage.Reduce.Reducer, stage.Id, limits, diagnostics);
                break;
            case WorkflowStageKind.Loop:
                if (stage.Step is not null
                    || stage.ForEach is not null
                    || stage.Reduce is not null
                    || stage.Loop is null)
                {
                    ShapeError(stage, diagnostics);
                    return;
                }

                var loop = stage.Loop;
                if (loop.MaxIterations > limits.MaxLoopIterations
                    || !WorkflowJson.IsValidPointer(loop.UntilPointer))
                {
                    diagnostics.Add(new WorkflowDiagnostic(
                        WorkflowReasonCodes.StageShapeInvalid,
                        $"Loop stage '{stage.Id}' has invalid bounds or JSON pointer.",
                        stage.Id));
                }

                ValidateStep(loop.Body, stage.Id, limits, diagnostics);
                AddSchemaDiagnostic(
                    diagnostics,
                    WorkflowSchema.ValidateDefinition(
                        loop.IterationInputSchema,
                        limits,
                        $"loop stage '{stage.Id}' iteration input schema",
                        stage.Id));
                AddSchemaDiagnostic(
                    diagnostics,
                    WorkflowSchema.ValidateDefinition(
                        loop.IterationOutputSchema,
                        limits,
                        $"loop stage '{stage.Id}' iteration output schema",
                        stage.Id));
                break;
            default:
                ShapeError(stage, diagnostics);
                break;
        }
    }

    private static void ValidateStep(
        WorkflowStepReference step,
        string stageId,
        WorkflowLimits limits,
        List<WorkflowDiagnostic> diagnostics)
    {
        if (step.Settings.ValueKind != JsonValueKind.Object
            || WorkflowJson.MeasureUtf8(step.Settings) > limits.MaxSchemaBytes)
        {
            diagnostics.Add(new WorkflowDiagnostic(
                WorkflowReasonCodes.JsonLimitExceeded,
                $"Step settings for '{stageId}' exceed their closed JSON boundary.",
                stageId));
            return;
        }

        try
        {
            _ = CanonicalJsonDigest.ComputeSha256(step.Settings);
        }
        catch (ArgumentException)
        {
            diagnostics.Add(new WorkflowDiagnostic(
                WorkflowReasonCodes.JsonLimitExceeded,
                $"Step settings for '{stageId}' cannot be canonicalized.",
                stageId));
        }
    }

    private static void ValidateDependencies(
        WorkflowStageDefinition stage,
        IReadOnlyDictionary<string, WorkflowStageDefinition> stagesById,
        WorkflowLimits limits,
        List<WorkflowDiagnostic> diagnostics)
    {
        if (stage.DependsOn.Count > limits.MaxDependenciesPerStage)
        {
            diagnostics.Add(new WorkflowDiagnostic(
                WorkflowReasonCodes.LimitExceeded,
                $"Stage '{stage.Id}' exceeds the dependency limit.",
                stage.Id));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in stage.DependsOn)
        {
            if (string.Equals(dependency, stage.Id, StringComparison.Ordinal))
            {
                diagnostics.Add(new WorkflowDiagnostic(
                    WorkflowReasonCodes.SelfDependency,
                    $"Stage '{stage.Id}' depends on itself.",
                    stage.Id));
            }
            else if (!stagesById.ContainsKey(dependency))
            {
                diagnostics.Add(new WorkflowDiagnostic(
                    WorkflowReasonCodes.UnknownDependency,
                    $"Stage '{stage.Id}' references unknown dependency '{dependency}'.",
                    stage.Id));
            }

            if (!seen.Add(dependency))
            {
                diagnostics.Add(new WorkflowDiagnostic(
                    WorkflowReasonCodes.StageShapeInvalid,
                    $"Stage '{stage.Id}' repeats dependency '{dependency}'.",
                    stage.Id));
            }
        }
    }

    private static IReadOnlyList<string> TopologicalSort(
        IReadOnlyDictionary<string, WorkflowStageDefinition> stagesById,
        List<WorkflowDiagnostic> diagnostics)
    {
        var indegree = stagesById.Keys.ToDictionary(
            id => id,
            _ => 0,
            StringComparer.Ordinal);
        var dependents = stagesById.Keys.ToDictionary(
            id => id,
            _ => new List<string>(),
            StringComparer.Ordinal);

        foreach (var stage in stagesById.Values)
        {
            foreach (var dependency in stage.DependsOn
                         .Distinct(StringComparer.Ordinal))
            {
                if (!stagesById.ContainsKey(dependency)
                    || string.Equals(
                        dependency,
                        stage.Id,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                indegree[stage.Id]++;
                dependents[dependency].Add(stage.Id);
            }
        }

        var ready = new SortedSet<string>(
            indegree
                .Where(item => item.Value == 0)
                .Select(item => item.Key),
            StringComparer.Ordinal);
        var result = new List<string>(stagesById.Count);
        while (ready.Count > 0)
        {
            var next = ready.Min!;
            ready.Remove(next);
            result.Add(next);
            foreach (var dependent in dependents[next]
                         .OrderBy(item => item, StringComparer.Ordinal))
            {
                indegree[dependent]--;
                if (indegree[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        if (result.Count != stagesById.Count)
        {
            diagnostics.Add(new WorkflowDiagnostic(
                WorkflowReasonCodes.DependencyCycle,
                "The workflow dependency graph contains a cycle."));
        }

        return new ReadOnlyCollection<string>(result);
    }

    private static void ValidateOutputStage(
        string outputStageId,
        IReadOnlyDictionary<string, WorkflowStageDefinition> stagesById,
        IReadOnlyList<string> topologicalIds,
        List<WorkflowDiagnostic> diagnostics)
    {
        if (!stagesById.ContainsKey(outputStageId))
        {
            diagnostics.Add(new WorkflowDiagnostic(
                WorkflowReasonCodes.OutputStageInvalid,
                "The output stage does not exist."));
            return;
        }

        if (topologicalIds.Count != stagesById.Count)
        {
            return;
        }

        var referenced = new HashSet<string>(
            stagesById.Values.SelectMany(stage => stage.DependsOn),
            StringComparer.Ordinal);
        var sinks = stagesById.Keys
            .Where(id => !referenced.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (sinks.Length != 1
            || !string.Equals(
                sinks[0],
                outputStageId,
                StringComparison.Ordinal))
        {
            diagnostics.Add(new WorkflowDiagnostic(
                WorkflowReasonCodes.OutputStageInvalid,
                "A workflow must have one declared terminal output stage."));
        }
    }

    private static string ComputeDefinitionDigest(WorkflowDefinition definition)
    {
        var payload = WorkflowJson.CreateElement(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("contract", "gameagent.workflow.definition.v1");
            writer.WriteString("id", definition.Id);
            writer.WriteString("version", definition.Version);
            writer.WriteString("outputStageId", definition.OutputStageId);
            writer.WritePropertyName("inputSchema");
            definition.InputSchema.WriteTo(writer);
            writer.WritePropertyName("outputSchema");
            definition.OutputSchema.WriteTo(writer);
            WriteLimits(writer, definition.Limits);
            writer.WritePropertyName("stages");
            writer.WriteStartArray();
            foreach (var stage in definition.Stages
                         .OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                WriteStage(writer, stage);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        return CanonicalJsonDigest.ComputeSha256(payload);
    }

    private static void WriteLimits(Utf8JsonWriter writer, WorkflowLimits limits)
    {
        writer.WritePropertyName("limits");
        writer.WriteStartObject();
        writer.WriteNumber("maxStages", limits.MaxStages);
        writer.WriteNumber(
            "maxDependenciesPerStage",
            limits.MaxDependenciesPerStage);
        writer.WriteNumber("maxParallelism", limits.MaxParallelism);
        writer.WriteNumber("maxForeachItems", limits.MaxForeachItems);
        writer.WriteNumber("maxLoopIterations", limits.MaxLoopIterations);
        writer.WriteNumber("maxStageExecutions", limits.MaxStageExecutions);
        writer.WriteNumber("maxStageAttempts", limits.MaxStageAttempts);
        writer.WriteNumber("maxInputBytes", limits.MaxInputBytes);
        writer.WriteNumber(
            "maxStageOutputBytes",
            limits.MaxStageOutputBytes);
        writer.WriteNumber(
            "maxRetainedOutputBytes",
            limits.MaxRetainedOutputBytes);
        writer.WriteNumber("maxDurationMs", limits.MaxDurationMs);
        writer.WriteNumber("maxSchemaDepth", limits.MaxSchemaDepth);
        writer.WriteNumber("maxSchemaBytes", limits.MaxSchemaBytes);
        writer.WriteEndObject();
    }

    private static void WriteStage(
        Utf8JsonWriter writer,
        WorkflowStageDefinition stage)
    {
        writer.WriteStartObject();
        writer.WriteString("id", stage.Id);
        writer.WriteString("kind", stage.Kind.ToString().ToLowerInvariant());
        writer.WritePropertyName("dependsOn");
        writer.WriteStartArray();
        foreach (var dependency in stage.DependsOn
                     .OrderBy(item => item, StringComparer.Ordinal))
        {
            writer.WriteStringValue(dependency);
        }

        writer.WriteEndArray();
        writer.WritePropertyName("inputSchema");
        stage.InputSchema.WriteTo(writer);
        writer.WritePropertyName("outputSchema");
        stage.OutputSchema.WriteTo(writer);
        switch (stage.Kind)
        {
            case WorkflowStageKind.Step:
                WriteStep(writer, "step", stage.Step!);
                break;
            case WorkflowStageKind.Foreach:
                var forEach = stage.ForEach!;
                writer.WritePropertyName("foreach");
                writer.WriteStartObject();
                writer.WriteString("sourcePointer", forEach.SourcePointer);
                writer.WriteString(
                    "itemIdentityPointer",
                    forEach.ItemIdentityPointer);
                writer.WriteNumber("maxItems", forEach.MaxItems);
                writer.WritePropertyName("itemInputSchema");
                forEach.ItemInputSchema.WriteTo(writer);
                writer.WritePropertyName("itemOutputSchema");
                forEach.ItemOutputSchema.WriteTo(writer);
                WriteStep(writer, "body", forEach.Body);
                writer.WriteEndObject();
                break;
            case WorkflowStageKind.Reduce:
                WriteStep(writer, "reducer", stage.Reduce!.Reducer);
                break;
            case WorkflowStageKind.Loop:
                var loop = stage.Loop!;
                writer.WritePropertyName("loop");
                writer.WriteStartObject();
                writer.WriteString("untilPointer", loop.UntilPointer);
                writer.WriteNumber("maxIterations", loop.MaxIterations);
                writer.WritePropertyName("iterationInputSchema");
                loop.IterationInputSchema.WriteTo(writer);
                writer.WritePropertyName("iterationOutputSchema");
                loop.IterationOutputSchema.WriteTo(writer);
                WriteStep(writer, "body", loop.Body);
                writer.WriteEndObject();
                break;
        }

        writer.WriteEndObject();
    }

    private static void WriteStep(
        Utf8JsonWriter writer,
        string propertyName,
        WorkflowStepReference step)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        writer.WriteString("kind", step.Kind);
        writer.WritePropertyName("settings");
        step.Settings.WriteTo(writer);
        writer.WriteEndObject();
    }

    private static void ShapeError(
        WorkflowStageDefinition stage,
        List<WorkflowDiagnostic> diagnostics)
    {
        diagnostics.Add(new WorkflowDiagnostic(
            WorkflowReasonCodes.StageShapeInvalid,
            $"Stage '{stage.Id}' does not match its declared kind.",
            stage.Id));
    }

    private static void AddSchemaDiagnostic(
        List<WorkflowDiagnostic> diagnostics,
        WorkflowDiagnostic? diagnostic)
    {
        if (diagnostic is not null)
        {
            diagnostics.Add(diagnostic);
        }
    }
}
