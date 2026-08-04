using GameAgent.Evaluation;

namespace GameAgent.Evaluation.Tests;

public sealed class GameAgentEvaluatorTests
{
    [Fact]
    public void PerfectEvidencePassesAllGates()
    {
        var result = new GameAgentEvaluator().Evaluate(Perfect());
        Assert.True(result.Passed);
        Assert.Equal(1, result.Score, 10);
        Assert.Empty(result.GateFailures);
    }

    [Fact]
    public void IllegalActionFailsHardGateEvenWhenOverallIsHigh()
    {
        var value = Perfect();
        value.ActionLegality = new EvaluationRatioEvidence { Passed = 9, Total = 10 };
        var result = new GameAgentEvaluator().Evaluate(value);
        Assert.False(result.Passed);
        Assert.Contains("criterion_below_minimum:action_legality", result.GateFailures);
    }

    [Fact]
    public void MissingRequiredEvidenceCannotPass()
    {
        var value = Perfect();
        value.WorldConsistency = null;
        var result = new GameAgentEvaluator().Evaluate(value);
        Assert.False(result.Passed);
        Assert.Contains("required_missing:world_consistency", result.GateFailures);
    }

    [Fact]
    public void MemoryUsesPrecisionRecallF1()
    {
        var value = Perfect();
        value.MemoryGrounding = new MemoryGroundingEvidence
        {
            RelevantCitations = 2,
            TotalCitations = 4,
            RelevantFactsAvailable = 2
        };
        var result = new GameAgentEvaluator().Evaluate(value);
        var memory = Assert.Single(result.Criteria, item => item.Criterion == EvaluationCriteria.MemoryGrounding);
        Assert.Equal(2d / 3d, memory.Score, 10);
    }

    [Fact]
    public void JsonLinesRunnerIsDeterministicAndBounded()
    {
        const string line = "{\"scenarioId\":\"case\",\"actionLegality\":{\"applicable\":true,\"passed\":1,\"total\":1},\"worldConsistency\":{\"applicable\":true,\"passed\":1,\"total\":1}}";
        var evaluator = new GameAgentEvaluator(new GameAgentEvaluationOptions { MinimumOverallScore = 0 });
        const string second = "{\"scenarioId\":\"case-2\",\"actionLegality\":{\"applicable\":true,\"passed\":1,\"total\":1},\"worldConsistency\":{\"applicable\":true,\"passed\":1,\"total\":1}}";
        var results = evaluator.EvaluateJsonLines(line + "\n" + second);
        Assert.Equal(2, results.Count);
        Assert.Equal(results[0].Score, results[1].Score);
        Assert.Throws<InvalidDataException>(() => evaluator.EvaluateJsonLines(line + "\n" + second, 1));
        Assert.Throws<InvalidDataException>(() => evaluator.EvaluateJsonLines(line + "\n" + line));
    }

    [Fact]
    public void JsonLinesRunnerRejectsUnknownEvidenceFields()
    {
        const string line = "{\"scenarioId\":\"case\",\"actionLegality\":{\"applicable\":true,\"passed\":1,\"total\":1,\"typo\":true}}";
        Assert.Throws<InvalidDataException>(() => new GameAgentEvaluator().EvaluateJsonLines(line));
    }

    [Fact]
    public void NonFiniteBudgetIsRejected()
    {
        var value = Perfect();
        value.Cost = new EvaluationBudgetEvidence { Observed = double.NaN, Target = 1, Maximum = 2 };
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameAgentEvaluator().Evaluate(value));
    }

    private static GameAgentEvaluationCase Perfect() => new()
    {
        ScenarioId = "scenario",
        CharacterAdherence = new EvaluationRatioEvidence { Passed = 2, Total = 2 },
        ActionLegality = new EvaluationRatioEvidence { Passed = 2, Total = 2 },
        MemoryGrounding = new MemoryGroundingEvidence(),
        WorldConsistency = new EvaluationRatioEvidence { Passed = 2, Total = 2 },
        LatencyMilliseconds = new EvaluationBudgetEvidence { Observed = 100, Target = 200, Maximum = 1_000 },
        Tokens = new EvaluationBudgetEvidence { Observed = 100, Target = 200, Maximum = 1_000 },
        Cost = new EvaluationBudgetEvidence { Observed = 0.01, Target = 0.02, Maximum = 0.1 }
    };
}
