using System.Text;
using System.Text.Json;

namespace GameAgent.Evaluation;

public sealed class GameAgentEvaluator
{
    private readonly GameAgentEvaluationOptions _options;

    public GameAgentEvaluator(GameAgentEvaluationOptions? options = null)
    {
        _options = (options ?? new GameAgentEvaluationOptions()).Snapshot();
    }

    public GameAgentEvaluationResult Evaluate(GameAgentEvaluationCase evaluationCase)
    {
        if (evaluationCase is null) throw new ArgumentNullException(nameof(evaluationCase));
        ValidateId(evaluationCase.ScenarioId);
        var values = new Dictionary<string, ScoreValue>(StringComparer.Ordinal)
        {
            [EvaluationCriteria.CharacterAdherence] = Ratio(evaluationCase.CharacterAdherence),
            [EvaluationCriteria.ActionLegality] = Ratio(evaluationCase.ActionLegality),
            [EvaluationCriteria.MemoryGrounding] = Memory(evaluationCase.MemoryGrounding),
            [EvaluationCriteria.WorldConsistency] = Ratio(evaluationCase.WorldConsistency),
            [EvaluationCriteria.Latency] = Budget(evaluationCase.LatencyMilliseconds),
            [EvaluationCriteria.TokenEfficiency] = Budget(evaluationCase.Tokens),
            [EvaluationCriteria.CostEfficiency] = Budget(evaluationCase.Cost)
        };

        var results = new List<EvaluationCriterionResult>(EvaluationCriteria.All.Length);
        var weighted = 0d;
        var evaluatedWeight = 0d;
        foreach (var criterion in EvaluationCriteria.All)
        {
            var value = values[criterion];
            var weight = _options.Weights.TryGetValue(criterion, out var configured) ? configured : 0;
            results.Add(new EvaluationCriterionResult(criterion, value.Evaluated, value.Score, weight, value.ReasonCode));
            if (value.Evaluated && weight > 0)
            {
                weighted += value.Score * weight;
                evaluatedWeight += weight;
            }
        }
        var score = evaluatedWeight == 0 ? 0 : weighted / evaluatedWeight;
        var gates = new List<string>();
        foreach (var required in _options.RequiredCriteria)
        {
            if (!values[required].Evaluated) gates.Add("required_missing:" + required);
        }
        GateMinimum(EvaluationCriteria.ActionLegality, _options.MinimumActionLegality);
        GateMinimum(EvaluationCriteria.WorldConsistency, _options.MinimumWorldConsistency);
        if (score < _options.MinimumOverallScore) gates.Add("overall_below_minimum");
        return new GameAgentEvaluationResult(evaluationCase.ScenarioId, score, gates.Count == 0, results, gates);

        void GateMinimum(string criterion, double minimum)
        {
            var value = values[criterion];
            if (value.Evaluated && value.Score < minimum) gates.Add("criterion_below_minimum:" + criterion);
        }
    }

    public IReadOnlyList<GameAgentEvaluationResult> EvaluateJsonLines(string jsonLines, int maximumCases = 10_000)
    {
        if (jsonLines is null) throw new ArgumentNullException(nameof(jsonLines));
        if (maximumCases is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maximumCases));
        var results = new List<GameAgentEvaluationResult>();
        var scenarioIds = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(jsonLines);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (Encoding.UTF8.GetByteCount(line) > 1_048_576) throw new InvalidDataException($"Evaluation line {lineNumber} exceeds its byte limit.");
            if (results.Count >= maximumCases) throw new InvalidDataException("The evaluation case limit was exceeded.");
            try
            {
                var item = JsonSerializer.Deserialize<GameAgentEvaluationCase>(line, JsonOptions)
                           ?? throw new JsonException("The evaluation case was null.");
                if (!scenarioIds.Add(item.ScenarioId))
                {
                    throw new ArgumentException("Evaluation scenario IDs must be unique.", nameof(jsonLines));
                }
                results.Add(Evaluate(item));
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                throw new InvalidDataException($"Evaluation line {lineNumber} is invalid.", exception);
            }
        }
        return results;
    }

    private static ScoreValue Ratio(EvaluationRatioEvidence? evidence)
    {
        if (evidence is null || !evidence.Applicable) return ScoreValue.Missing;
        if (evidence.Total < 1 || evidence.Passed < 0 || evidence.Passed > evidence.Total) throw new ArgumentOutOfRangeException(nameof(evidence));
        return new ScoreValue(true, (double)evidence.Passed / evidence.Total, "ratio");
    }

    private static ScoreValue Memory(MemoryGroundingEvidence? evidence)
    {
        if (evidence is null || !evidence.Applicable) return ScoreValue.Missing;
        if (evidence.RelevantCitations < 0 || evidence.TotalCitations < 0 || evidence.RelevantFactsAvailable < 0
            || evidence.RelevantCitations > evidence.TotalCitations
            || evidence.RelevantCitations > evidence.RelevantFactsAvailable)
        {
            throw new ArgumentOutOfRangeException(nameof(evidence));
        }
        if (evidence.TotalCitations == 0 && evidence.RelevantFactsAvailable == 0) return new ScoreValue(true, 1, "no_memory_needed");
        if (evidence.TotalCitations == 0 || evidence.RelevantFactsAvailable == 0) return new ScoreValue(true, 0, "ungrounded_or_missing");
        var precision = (double)evidence.RelevantCitations / evidence.TotalCitations;
        var recall = (double)evidence.RelevantCitations / evidence.RelevantFactsAvailable;
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        return new ScoreValue(true, f1, "citation_f1");
    }

    private static ScoreValue Budget(EvaluationBudgetEvidence? evidence)
    {
        if (evidence is null || !evidence.Applicable) return ScoreValue.Missing;
        if (!FiniteNonNegative(evidence.Observed) || !FiniteNonNegative(evidence.Target)
            || !FiniteNonNegative(evidence.Maximum) || evidence.Maximum <= evidence.Target)
        {
            throw new ArgumentOutOfRangeException(nameof(evidence));
        }
        var score = evidence.Observed <= evidence.Target
            ? 1
            : evidence.Observed >= evidence.Maximum
                ? 0
                : 1 - (evidence.Observed - evidence.Target) / (evidence.Maximum - evidence.Target);
        return new ScoreValue(true, score, "bounded_budget");
    }

    private static bool FiniteNonNegative(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;

    private static void ValidateId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) throw new ArgumentException("A bounded scenario ID is required.", nameof(value));
    }

    private sealed class ScoreValue
    {
        public static readonly ScoreValue Missing = new(false, 0, "not_applicable");
        public ScoreValue(bool evaluated, double score, string reasonCode)
        {
            Evaluated = evaluated;
            Score = score;
            ReasonCode = reasonCode;
        }
        public bool Evaluated { get; }
        public double Score { get; }
        public string ReasonCode { get; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 64,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
}
