using Xunit;

namespace GameAgent.Workflow.Tests;

public sealed class WorkflowCompilerTests
{
    [Fact]
    public void Compile_IsDeclarationOrderIndependentAndDeterministic()
    {
        var first = Definition(reverseStages: false, reverseDependencies: false);
        var second = Definition(reverseStages: true, reverseDependencies: true);

        var compiler = new WorkflowCompiler();
        var left = compiler.Compile(first);
        var right = compiler.Compile(second);

        Assert.Equal(left.DefinitionDigest, right.DefinitionDigest);
        Assert.Equal(
            new[] { "a", "b", "reduce" },
            left.Stages.Select(item => item.Definition.Id));
        Assert.Equal(
            left.Stages.Select(item => item.Definition.Id),
            right.Stages.Select(item => item.Definition.Id));

        var inputLeft = WorkflowTestData.Json("""{"seed":"x"}""");
        var inputRight = WorkflowTestData.Json("""{"seed":"x"}""");
        var leftRun = WorkflowIdentity.CreateRunId(
            left.DefinitionDigest,
            WorkflowIdentity.ComputeJsonDigest(inputLeft),
            "same-run");
        var rightRun = WorkflowIdentity.CreateRunId(
            right.DefinitionDigest,
            WorkflowIdentity.ComputeJsonDigest(inputRight),
            "same-run");
        Assert.Equal(leftRun, rightRun);
        Assert.Equal(
            WorkflowIdentity.ComputeJsonDigest(
                WorkflowTestData.Json("""{"a":1,"b":2}""")),
            WorkflowIdentity.ComputeJsonDigest(
                WorkflowTestData.Json("""{"b":2,"a":1}""")));
    }

    [Fact]
    public void Compile_RejectsUnknownSelfCycleDuplicateAndMultipleSinks()
    {
        var schema = WorkflowTestData.StringSchema();
        var step = new WorkflowStepReference("test/pass");
        var stages = new[]
        {
            WorkflowStageDefinition.CreateStep(
                "duplicate",
                step,
                schema,
                schema,
                new[] { "missing" }),
            WorkflowStageDefinition.CreateStep(
                "duplicate",
                step,
                schema,
                schema),
            WorkflowStageDefinition.CreateStep(
                "self",
                step,
                schema,
                schema,
                new[] { "self" }),
            WorkflowStageDefinition.CreateStep(
                "cycle-a",
                step,
                schema,
                schema,
                new[] { "cycle-b" }),
            WorkflowStageDefinition.CreateStep(
                "cycle-b",
                step,
                schema,
                schema,
                new[] { "cycle-a" })
        };
        var definition = new WorkflowDefinition(
            "invalid",
            "v1",
            schema,
            schema,
            "duplicate",
            stages);

        var exception = Assert.Throws<WorkflowCompilationException>(
            () => new WorkflowCompiler().Compile(definition));
        var codes = exception.Diagnostics.Select(item => item.Code).ToHashSet();

        Assert.Contains(WorkflowReasonCodes.DuplicateStageId, codes);
        Assert.Contains(WorkflowReasonCodes.UnknownDependency, codes);
        Assert.Contains(WorkflowReasonCodes.SelfDependency, codes);
        Assert.Contains(WorkflowReasonCodes.DependencyCycle, codes);
    }

    [Fact]
    public void Compile_RejectsOpenOrUnboundedSchemas()
    {
        var invalid = WorkflowTestData.Json(
            """
            {
              "type": "object",
              "properties": {
                "value": {
                  "type": "string"
                }
              }
            }
            """);
        var stage = WorkflowStageDefinition.CreateStep(
            "only",
            new WorkflowStepReference("test/pass"),
            invalid,
            invalid);
        var definition = new WorkflowDefinition(
            "schema-invalid",
            "v1",
            invalid,
            invalid,
            "only",
            new[] { stage });

        var exception = Assert.Throws<WorkflowCompilationException>(
            () => new WorkflowCompiler().Compile(definition));

        Assert.Contains(
            exception.Diagnostics,
            item => item.Code == WorkflowReasonCodes.SchemaInvalid);
    }

    [Fact]
    public void ConstructorsEnumerateInsteadOfTrustingReportedCount()
    {
        var schema = WorkflowTestData.StringSchema();
        var stage = WorkflowStageDefinition.CreateStep(
            "only",
            new WorkflowStepReference("test/pass"),
            schema,
            schema,
            new LyingCollection<string>());
        var definition = new WorkflowDefinition(
            "lying",
            "v1",
            schema,
            schema,
            "only",
            new LyingCollection<WorkflowStageDefinition>(stage));

        var compiled = new WorkflowCompiler().Compile(definition);

        Assert.Single(compiled.Stages);
        Assert.Equal("only", compiled.Stages[0].Definition.Id);
    }

    private static WorkflowDefinition Definition(
        bool reverseStages,
        bool reverseDependencies)
    {
        var seed = WorkflowTestData.SeedSchema();
        var text = WorkflowTestData.StringSchema();
        var reduceInput = WorkflowTestData.ReduceInputSchema();
        var a = WorkflowStageDefinition.CreateStep(
            "a",
            new WorkflowStepReference(
                "test/value",
                WorkflowTestData.Json("""{"value":"A"}""")),
            seed,
            text);
        var b = WorkflowStageDefinition.CreateStep(
            "b",
            new WorkflowStepReference(
                "test/value",
                WorkflowTestData.Json("""{"value":"B"}""")),
            seed,
            text);
        var dependencies = reverseDependencies
            ? new[] { "b", "a" }
            : new[] { "a", "b" };
        var reduce = WorkflowStageDefinition.CreateReduce(
            "reduce",
            new WorkflowReduceDefinition(
                new WorkflowStepReference("test/reduce")),
            reduceInput,
            text,
            dependencies);
        var stages = reverseStages
            ? new[] { reduce, b, a }
            : new[] { a, b, reduce };
        return new WorkflowDefinition(
            "deterministic",
            "v1",
            seed,
            text,
            "reduce",
            stages);
    }
}
