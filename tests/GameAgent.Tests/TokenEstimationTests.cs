using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class TokenEstimationTests
{
    [Fact]
    public void ScriptAwareEstimatorChargesCjkAndJsonStructure()
    {
        var estimator = new ScriptAwareTokenEstimator();

        var cjk = estimator.EstimateTokens("""{"内容":"山海人物行动"}""");
        var ascii = estimator.EstimateTokens(
            """{"content":"ordinary words"}""");

        Assert.True(cjk >= 10);
        Assert.True(ascii >= 8);
        Assert.True(
            estimator.EstimateOpaqueUtf8Bytes(3_000) >= 1_500);
    }

    [Fact]
    public void ScriptAwareEstimatorRejectsMalformedUnicode()
    {
        var estimator = new ScriptAwareTokenEstimator();

        Assert.Throws<ArgumentException>(
            () => estimator.EstimateTokens("\ud800"));
    }

    [Fact]
    public void CalibrationOnlyRaisesABoundedSafetyMultiplier()
    {
        var estimator = new CalibratingProviderTokenEstimator(
            new FixedEstimator(10));
        var messages = new[]
        {
            Message("message-1", """{"value":"test"}""")
        };

        var baseline = estimator.EstimatePromptTokens(
            messages,
            Array.Empty<ToolDescriptor>());
        estimator.ObserveActualInputTokens(baseline, baseline * 2);
        var raised = estimator.EstimatePromptTokens(
            messages,
            Array.Empty<ToolDescriptor>());
        estimator.ObserveActualInputTokens(raised, 1);

        Assert.True(raised > baseline);
        Assert.Equal(
            raised,
            estimator.EstimatePromptTokens(
                messages,
                Array.Empty<ToolDescriptor>()));
        Assert.InRange(estimator.CurrentMultiplier, 1.0, 4.0);
    }

    [Fact]
    public void ContextCompilerUsesConfiguredEstimator()
    {
        var compiler = new ContextCompiler(
            new ContextCompilerOptions(
                maxCandidates: 4,
                maxSelectedItems: 4,
                maxEstimatedTokens: 100,
                maxUtf8Bytes: 8_192),
            new FixedEstimator(17));
        var candidate = new ContextCandidate(
            "typed-state",
            "world_state",
            Json("""{"turn":12}"""),
            priority: 1,
            required: true,
            canDefer: false);

        var compiled = compiler.Compile(
            new ContextCompilationRequest(
                "run-1",
                "turn-1",
                new[] { candidate },
                DateTimeOffset.UnixEpoch));

        Assert.Equal(17, compiled.BudgetReport.EstimatedTokens);
    }

    [Fact]
    public void PromptMeasurementRecordsEstimatorIdentity()
    {
        var measurement = RuntimePromptBuilder.MeasurePrompt(
            new[] { Message("message-1", """{"value":"test"}""") },
            Array.Empty<ToolDescriptor>(),
            maxMessages: 8,
            maxUtf8Bytes: 8_192,
            estimatedBytesPerToken: 4,
            tokenEstimator: new FixedEstimator(13));
        var evidence = RuntimePromptBuilder.PromptMeasurementEvidence(
            measurement);

        Assert.Equal(13, measurement.EstimatedTokens);
        Assert.Equal(
            "fixed",
            evidence.GetProperty("estimatorId").GetString());
        Assert.Equal(
            "1",
            evidence.GetProperty("estimatorVersion").GetString());
    }

    private static NormalizedMessage Message(
        string messageId,
        string json)
    {
        return new NormalizedMessage
        {
            MessageId = messageId,
            Role = NormalizedRoles.User,
            CreatedAt = DateTimeOffset.UnixEpoch,
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromJson(Json(json))
            }
        };
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FixedEstimator : IRuntimeTokenEstimator
    {
        private readonly int _tokens;

        public FixedEstimator(int tokens)
        {
            _tokens = tokens;
        }

        public string EstimatorId => "fixed";

        public string Version => "1";

        public int EstimateTokens(string content)
        {
            _ = content ?? throw new ArgumentNullException(nameof(content));
            return content.Length == 0 ? 0 : _tokens;
        }

        public int EstimateOpaqueUtf8Bytes(int utf8Bytes)
        {
            if (utf8Bytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(utf8Bytes));
            }

            return utf8Bytes == 0 ? 0 : _tokens;
        }
    }
}
