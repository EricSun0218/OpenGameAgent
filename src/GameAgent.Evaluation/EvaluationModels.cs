namespace GameAgent.Evaluation;

public static class EvaluationCriteria
{
    public const string CharacterAdherence = "character_adherence";
    public const string ActionLegality = "action_legality";
    public const string MemoryGrounding = "memory_grounding";
    public const string WorldConsistency = "world_consistency";
    public const string Latency = "latency";
    public const string TokenEfficiency = "token_efficiency";
    public const string CostEfficiency = "cost_efficiency";

    internal static readonly string[] All =
    {
        CharacterAdherence,
        ActionLegality,
        MemoryGrounding,
        WorldConsistency,
        Latency,
        TokenEfficiency,
        CostEfficiency
    };
}

public sealed class EvaluationRatioEvidence
{
    public bool Applicable { get; set; } = true;
    public int Passed { get; set; }
    public int Total { get; set; }
}

public sealed class MemoryGroundingEvidence
{
    public bool Applicable { get; set; } = true;
    public int RelevantCitations { get; set; }
    public int TotalCitations { get; set; }
    public int RelevantFactsAvailable { get; set; }
}

public sealed class EvaluationBudgetEvidence
{
    public bool Applicable { get; set; } = true;
    public double Observed { get; set; }
    public double Target { get; set; }
    public double Maximum { get; set; }
}

public sealed class GameAgentEvaluationCase
{
    public string ScenarioId { get; set; } = string.Empty;
    public EvaluationRatioEvidence? CharacterAdherence { get; set; }
    public EvaluationRatioEvidence? ActionLegality { get; set; }
    public MemoryGroundingEvidence? MemoryGrounding { get; set; }
    public EvaluationRatioEvidence? WorldConsistency { get; set; }
    public EvaluationBudgetEvidence? LatencyMilliseconds { get; set; }
    public EvaluationBudgetEvidence? Tokens { get; set; }
    public EvaluationBudgetEvidence? Cost { get; set; }
}

public sealed class GameAgentEvaluationOptions
{
    public IDictionary<string, double> Weights { get; set; } = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        [EvaluationCriteria.CharacterAdherence] = 0.2,
        [EvaluationCriteria.ActionLegality] = 0.25,
        [EvaluationCriteria.MemoryGrounding] = 0.15,
        [EvaluationCriteria.WorldConsistency] = 0.2,
        [EvaluationCriteria.Latency] = 0.1,
        [EvaluationCriteria.TokenEfficiency] = 0.05,
        [EvaluationCriteria.CostEfficiency] = 0.05
    };

    public ISet<string> RequiredCriteria { get; set; } = new HashSet<string>(StringComparer.Ordinal)
    {
        EvaluationCriteria.ActionLegality,
        EvaluationCriteria.WorldConsistency
    };

    public double MinimumOverallScore { get; set; } = 0.75;
    public double MinimumActionLegality { get; set; } = 1;
    public double MinimumWorldConsistency { get; set; } = 1;

    internal GameAgentEvaluationOptions Snapshot()
    {
        if (Weights is null || RequiredCriteria is null) throw new ArgumentNullException(nameof(Weights));
        var known = new HashSet<string>(EvaluationCriteria.All, StringComparer.Ordinal);
        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var pair in Weights)
        {
            if (!known.Contains(pair.Key) || !FiniteUnit(pair.Value)) throw new ArgumentOutOfRangeException(nameof(Weights));
            weights.Add(pair.Key, pair.Value);
        }
        if (weights.Count == 0 || weights.Values.Sum() <= 0) throw new ArgumentException("At least one positive evaluation weight is required.", nameof(Weights));
        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var criterion in RequiredCriteria)
        {
            if (!known.Contains(criterion)) throw new ArgumentException("A required criterion is unknown.", nameof(RequiredCriteria));
            required.Add(criterion);
        }
        if (!FiniteUnit(MinimumOverallScore) || !FiniteUnit(MinimumActionLegality)
            || !FiniteUnit(MinimumWorldConsistency))
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumOverallScore));
        }
        return new GameAgentEvaluationOptions
        {
            Weights = weights,
            RequiredCriteria = required,
            MinimumOverallScore = MinimumOverallScore,
            MinimumActionLegality = MinimumActionLegality,
            MinimumWorldConsistency = MinimumWorldConsistency
        };
    }

    private static bool FiniteUnit(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value is >= 0 and <= 1;
}

public sealed class EvaluationCriterionResult
{
    internal EvaluationCriterionResult(string criterion, bool evaluated, double score, double weight, string reasonCode)
    {
        Criterion = criterion;
        Evaluated = evaluated;
        Score = score;
        Weight = weight;
        ReasonCode = reasonCode;
    }

    public string Criterion { get; }
    public bool Evaluated { get; }
    public double Score { get; }
    public double Weight { get; }
    public string ReasonCode { get; }
}

public sealed class GameAgentEvaluationResult
{
    internal GameAgentEvaluationResult(
        string scenarioId,
        double score,
        bool passed,
        IReadOnlyList<EvaluationCriterionResult> criteria,
        IReadOnlyList<string> gateFailures)
    {
        ScenarioId = scenarioId;
        Score = score;
        Passed = passed;
        Criteria = criteria;
        GateFailures = gateFailures;
    }

    public string ScenarioId { get; }
    public double Score { get; }
    public bool Passed { get; }
    public IReadOnlyList<EvaluationCriterionResult> Criteria { get; }
    public IReadOnlyList<string> GateFailures { get; }
}
