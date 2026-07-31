using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class SemanticToolLoopGuardTests
{
    [Fact]
    public void RepeatedTerminalFailureWarnsThenStopsAndRebuildsDeterministically()
    {
        var transcript = new List<NormalizedMessage>();
        var guard = SemanticToolLoopGuard.Rebuild(
            new SemanticToolLoopGuardOptions(),
            transcript);

        for (var index = 0; index < 3; index++)
        {
            var exchange = TerminalFailureExchange(index);
            transcript.AddRange(exchange);
            guard.ObserveMessages(exchange);
        }

        var warningDecision = Assert.IsType<SemanticToolLoopGuardDecision>(
            guard.Decision);
        Assert.False(warningDecision.HardStop);
        Assert.Equal(2, warningDecision.RepetitionCount);
        var warning = Assert.IsType<NormalizedMessage>(
            guard.CreateWarningMessage());
        Assert.DoesNotContain(
            "secret-argument",
            NormalizedMessageJournalCodec.Encode(warning).GetRawText(),
            StringComparison.Ordinal);

        for (var index = 3; index < 5; index++)
        {
            var exchange = TerminalFailureExchange(index);
            transcript.AddRange(exchange);
            guard.ObserveMessages(exchange);
        }

        var hardDecision = Assert.IsType<SemanticToolLoopGuardDecision>(
            guard.Decision);
        Assert.True(hardDecision.HardStop);
        Assert.Equal(4, hardDecision.RepetitionCount);
        Assert.Null(guard.CreateWarningMessage());

        var rebuilt = SemanticToolLoopGuard.Rebuild(
            new SemanticToolLoopGuardOptions(),
            transcript);
        var rebuiltDecision =
            Assert.IsType<SemanticToolLoopGuardDecision>(rebuilt.Decision);
        Assert.Equal(
            hardDecision.CallSignatureDigest,
            rebuiltDecision.CallSignatureDigest);
        Assert.Equal(hardDecision.OutcomeDigest, rebuiltDecision.OutcomeDigest);
        Assert.Equal(hardDecision.RepetitionCount, rebuiltDecision.RepetitionCount);
        Assert.True(rebuiltDecision.HardStop);
    }

    [Fact]
    public void IdenticalPureReadWarnsButChangingOutcomeIsProgress()
    {
        var guard = NewGuard();
        for (var index = 0; index < 3; index++)
        {
            guard.ObserveMessages(
                Exchange(
                    index,
                    ToolEffects.PureRead,
                    """{"value":7}"""));
        }

        Assert.NotNull(guard.Decision);

        guard.ObserveMessages(
            Exchange(
                3,
                ToolEffects.PureRead,
                """{"value":8}"""));

        Assert.Null(guard.Decision);

        for (var index = 4; index < 7; index++)
        {
            guard.ObserveMessages(
                Exchange(
                    index,
                    ToolEffects.PureRead,
                    "\"same-value\""));
        }

        Assert.NotNull(guard.Decision);
    }

    [Fact]
    public void SuccessfulWriteAndAuthoritativeProgressResetStableReads()
    {
        var guard = NewGuard();
        RepeatToWarning(guard, Receipt(
            ReceiptStatuses.Succeeded,
            revision: 7,
            result: """{"value":7}"""));
        Assert.NotNull(guard.Decision);

        guard.ObserveMessages(
            Exchange(
                20,
                ToolEffects.WorldCommand,
                Receipt(
                    ReceiptStatuses.Succeeded,
                    revision: 8,
                    result: """{"moved":true}""")));
        Assert.Null(guard.Decision);

        RepeatToWarning(guard, Receipt(
            ReceiptStatuses.Succeeded,
            revision: 8,
            result: """{"value":7}"""));
        Assert.NotNull(guard.Decision);

        guard.ObserveMessages(
            Exchange(
                21,
                ToolEffects.PureRead,
                """
                {
                  "status":"succeeded",
                  "revision":8,
                  "result":{"value":7},
                  "stateDiff":{},
                  "authoritativeObservations":[],
                  "retryable":false
                }
                """));
        Assert.Null(guard.Decision);

        RepeatToWarning(guard, Receipt(
            ReceiptStatuses.Succeeded,
            revision: 8,
            result: """{"value":7}"""));
        Assert.NotNull(guard.Decision);

        guard.ObserveMessages(
            Exchange(
                22,
                ToolEffects.PureRead,
                """
                {
                  "status":"succeeded",
                  "revision":8,
                  "result":{"value":7},
                  "stateDiff":null,
                  "authoritativeObservations":[{"observationId":"new"}],
                  "retryable":false
                }
                """));
        Assert.Null(guard.Decision);
    }

    [Fact]
    public void RevisionChangeResetsARepeatedPureReadOutcome()
    {
        var guard = NewGuard();
        RepeatToWarning(guard, Receipt(
            ReceiptStatuses.Succeeded,
            revision: 11,
            result: """{"value":7}"""));
        Assert.NotNull(guard.Decision);

        guard.ObserveMessages(
            Exchange(
                30,
                ToolEffects.PureRead,
                Receipt(
                    ReceiptStatuses.Succeeded,
                    revision: 12,
                    result: """{"value":7}""")));

        Assert.Null(guard.Decision);
    }

    [Fact]
    public void UnknownOrPendingOutcomeNeverTriggersAStop()
    {
        var guard = NewGuard();
        RepeatToWarning(guard, """{"code":"blocked","category":"tool","message":"same"}""");
        Assert.NotNull(guard.Decision);

        guard.ObserveMessages(
            Exchange(
                40,
                ToolEffects.PureRead,
                Receipt(
                    ReceiptStatuses.Unknown,
                    revision: 0,
                    result: "null")));
        Assert.Null(guard.Decision);

        var pending = CallMessage(
            41,
            ToolEffects.PureRead,
            """{"query":"secret-argument"}""");
        guard.ObserveMessages(new[] { pending });
        Assert.Null(guard.Decision);
    }

    [Fact]
    public void BoundedCapacityEvictsOldestPatternWithoutFalseStop()
    {
        var guard = SemanticToolLoopGuard.Rebuild(
            new SemanticToolLoopGuardOptions
            {
                MaxTrackedSignatures = 1
            },
            Array.Empty<NormalizedMessage>());

        guard.ObserveMessages(Exchange(
            50,
            ToolEffects.PureRead,
            """{"value":"a"}""",
            toolName: "read.a"));
        guard.ObserveMessages(Exchange(
            51,
            ToolEffects.PureRead,
            """{"value":"b"}""",
            toolName: "read.b"));
        guard.ObserveMessages(Exchange(
            52,
            ToolEffects.PureRead,
            """{"value":"a"}""",
            toolName: "read.a"));

        Assert.Null(guard.Decision);
    }

    [Fact]
    public void AlternatingSignaturesAccumulateIndependently()
    {
        var guard = NewGuard();
        var names = new[]
        {
            "read.a",
            "read.b",
            "read.a",
            "read.b",
            "read.a"
        };
        for (var index = 0; index < names.Length; index++)
        {
            guard.ObserveMessages(Exchange(
                60 + index,
                ToolEffects.PureRead,
                """{"value":7}""",
                names[index]));
        }

        var firstWarning = Assert.IsType<SemanticToolLoopGuardDecision>(
            guard.Decision);
        Assert.Equal("read.a", firstWarning.ToolName);
        Assert.Equal(2, firstWarning.RepetitionCount);

        guard.ObserveMessages(Exchange(
            65,
            ToolEffects.PureRead,
            """{"value":7}""",
            "read.b"));
        var secondWarning = Assert.IsType<SemanticToolLoopGuardDecision>(
            guard.Decision);
        Assert.Equal("read.b", secondWarning.ToolName);
        Assert.Equal(2, secondWarning.RepetitionCount);

        guard.ObserveMessages(Exchange(
            66,
            ToolEffects.PureRead,
            """{"value":7}""",
            "read.a"));
        guard.ObserveMessages(Exchange(
            67,
            ToolEffects.PureRead,
            """{"value":7}""",
            "read.b"));
        guard.ObserveMessages(Exchange(
            68,
            ToolEffects.PureRead,
            """{"value":7}""",
            "read.a"));

        var hardStop = Assert.IsType<SemanticToolLoopGuardDecision>(
            guard.Decision);
        Assert.Equal("read.a", hardStop.ToolName);
        Assert.Equal(4, hardStop.RepetitionCount);
        Assert.True(hardStop.HardStop);
    }

    [Fact]
    public void DistinctArgumentChurnWarnsThenStopsWithoutLeakingArguments()
    {
        var options = new SemanticToolLoopGuardOptions
        {
            ArgumentChurnWarningRepetitions = 2,
            ArgumentChurnHardStopRepetitions = 4
        };
        var transcript = new List<NormalizedMessage>();
        var guard = SemanticToolLoopGuard.Rebuild(options, transcript);
        const string sameFailure =
            """{"code":"not_found","category":"tool","message":"missing"}""";

        for (var index = 0; index < 3; index++)
        {
            var exchange = Exchange(
                100 + index,
                ToolEffects.PureRead,
                sameFailure,
                arguments: $$"""{"query":"secret-{{index}}"}""");
            transcript.AddRange(exchange);
            guard.ObserveMessages(exchange);
        }

        var warningDecision = Assert.IsType<SemanticToolLoopGuardDecision>(
            guard.Decision);
        Assert.Equal(
            SemanticToolLoopGuard.ArgumentChurnPatternKind,
            warningDecision.PatternKind);
        Assert.Equal(
            SemanticToolLoopGuard.ArgumentChurnWarningReasonCode,
            warningDecision.WarningReasonCode);
        Assert.False(warningDecision.HardStop);
        var warning = Assert.IsType<NormalizedMessage>(
            guard.CreateWarningMessage());
        var encoded = NormalizedMessageJournalCodec.Encode(warning)
            .GetRawText();
        Assert.DoesNotContain("secret-", encoded, StringComparison.Ordinal);
        Assert.Contains("argument_churn", encoded, StringComparison.Ordinal);
        Assert.Equal(
            4,
            warning.Parts.Single().Json!.Value
                .GetProperty("hardStopRepetitions")
                .GetInt32());

        for (var index = 3; index < 5; index++)
        {
            var exchange = Exchange(
                100 + index,
                ToolEffects.PureRead,
                sameFailure,
                arguments: $$"""{"query":"secret-{{index}}"}""");
            transcript.AddRange(exchange);
            guard.ObserveMessages(exchange);
        }

        var hardStop = Assert.IsType<SemanticToolLoopGuardDecision>(
            guard.Decision);
        Assert.True(hardStop.HardStop);
        Assert.Equal(4, hardStop.RepetitionCount);
        Assert.Equal(4, hardStop.HardStopRepetitions);
        Assert.DoesNotContain(
            "secret-",
            guard.SafeDiagnostic().GetRawText(),
            StringComparison.Ordinal);

        var rebuilt = SemanticToolLoopGuard.Rebuild(options, transcript);
        var rebuiltDecision = Assert.IsType<SemanticToolLoopGuardDecision>(
            rebuilt.Decision);
        Assert.Equal(hardStop.PatternDigest, rebuiltDecision.PatternDigest);
        Assert.Equal(hardStop.RepetitionCount, rebuiltDecision.RepetitionCount);
        Assert.True(rebuiltDecision.HardStop);
    }

    [Fact]
    public void ChangedOutcomeResetsArgumentChurn()
    {
        var guard = SemanticToolLoopGuard.Rebuild(
            new SemanticToolLoopGuardOptions
            {
                ArgumentChurnWarningRepetitions = 2,
                ArgumentChurnHardStopRepetitions = 4
            },
            Array.Empty<NormalizedMessage>());
        for (var index = 0; index < 2; index++)
        {
            guard.ObserveMessages(Exchange(
                120 + index,
                ToolEffects.PureRead,
                """{"value":0}""",
                arguments: $$"""{"page":{{index}}}"""));
        }

        guard.ObserveMessages(Exchange(
            122,
            ToolEffects.PureRead,
            """{"value":1}""",
            arguments: """{"page":2}"""));

        Assert.Null(guard.Decision);
    }

    [Fact]
    public void OversizedAndLegacySuccessfulEvidenceFailOpen()
    {
        var oversized = SemanticToolLoopGuard.Rebuild(
            new SemanticToolLoopGuardOptions
            {
                MaxDigestJsonUtf8Bytes = 32
            },
            Array.Empty<NormalizedMessage>());
        for (var index = 0; index < 8; index++)
        {
            oversized.ObserveMessages(
                Exchange(
                    index,
                    ToolEffects.PureRead,
                    """{"value":"this result is deliberately much too large"}"""));
        }

        Assert.Null(oversized.Decision);

        var legacy = NewGuard();
        for (var index = 0; index < 8; index++)
        {
            legacy.ObserveMessages(
                Exchange(
                    index + 20,
                    effect: null,
                    """{"value":7}"""));
        }

        Assert.Null(legacy.Decision);
    }

    [Fact]
    public void JournalCodecRoundTripsOptionalToolEvidenceAndReadsLegacy()
    {
        var message = CallMessage(
            70,
            ToolEffects.PureRead,
            """{"query":"secret-argument"}""");
        var encoded = NormalizedMessageJournalCodec.Encode(message);
        var decoded = NormalizedMessageJournalCodec.Decode(encoded);
        var part = Assert.Single(decoded.Parts);

        Assert.Equal("1", part.ToolVersion);
        Assert.Equal(ToolEffects.PureRead, part.ToolEffect);
        Assert.Equal("descriptor-digest", part.ToolDescriptorDigest);

        var legacy = Parse(
            """
            {
              "messageId":"legacy",
              "role":"assistant",
              "createdAt":"2026-01-01T00:00:00Z",
              "parts":[{
                "type":"tool_call",
                "json":{"query":"x"},
                "toolCallId":"call-legacy",
                "toolName":"read"
              }]
            }
            """);
        var legacyPart = Assert.Single(
            NormalizedMessageJournalCodec.Decode(legacy).Parts);
        Assert.Null(legacyPart.ToolVersion);
        Assert.Null(legacyPart.ToolEffect);
        Assert.Null(legacyPart.ToolDescriptorDigest);
    }

    [Fact]
    public void JournalCodecRejectsInvalidToolEvidence()
    {
        var invalid = Parse(
            """
            {
              "messageId":"invalid",
              "role":"assistant",
              "createdAt":"2026-01-01T00:00:00Z",
              "parts":[{
                "type":"tool_call",
                "json":{},
                "toolCallId":"call-invalid",
                "toolName":"read",
                "toolEffect":"surprise_write"
              }]
            }
            """);

        Assert.Throws<InvalidDataException>(
            () => NormalizedMessageJournalCodec.Decode(invalid));
    }

    private static SemanticToolLoopGuard NewGuard()
    {
        return SemanticToolLoopGuard.Rebuild(
            new SemanticToolLoopGuardOptions(),
            Array.Empty<NormalizedMessage>());
    }

    private static void RepeatToWarning(
        SemanticToolLoopGuard guard,
        string result)
    {
        for (var index = 0; index < 3; index++)
        {
            guard.ObserveMessages(
                Exchange(index + 10, ToolEffects.PureRead, result));
        }
    }

    private static IReadOnlyList<NormalizedMessage> TerminalFailureExchange(
        int index)
    {
        return Exchange(
            index,
            ToolEffects.WorldCommand,
            $$"""
              {
                "status":"failed",
                "revision":0,
                "result":null,
                "stateDiff":null,
                "authoritativeObservations":[],
                "errorCode":"blocked",
                "retryable":false,
                "operationId":"volatile-operation-{{index}}",
                "receivedAt":"2026-01-01T00:00:0{{index}}Z",
                "committedAt":"2026-01-01T00:01:0{{index}}Z"
              }
              """);
    }

    private static IReadOnlyList<NormalizedMessage> Exchange(
        int index,
        string? effect,
        string result,
        string toolName = "game.read",
        string arguments = """{"query":"secret-argument"}""")
    {
        var call = CallMessage(
            index,
            effect,
            arguments,
            toolName);
        return new[]
        {
            call,
            new NormalizedMessage
            {
                MessageId = "result-" + index,
                Role = NormalizedRoles.Tool,
                CreatedAt = new DateTimeOffset(
                    2026,
                    1,
                    1,
                    0,
                    0,
                    index % 60,
                    TimeSpan.Zero),
                Parts = new List<NormalizedContentPart>
                {
                    NormalizedContentPart.FromToolResult(
                        "call-" + index,
                        toolName,
                        Parse(result))
                }
            }
        };
    }

    private static NormalizedMessage CallMessage(
        int index,
        string? effect,
        string arguments,
        string toolName = "game.read")
    {
        return new NormalizedMessage
        {
            MessageId = "assistant-" + index,
            Role = NormalizedRoles.Assistant,
            CreatedAt = new DateTimeOffset(
                2026,
                1,
                1,
                0,
                0,
                index % 60,
                TimeSpan.Zero),
            Parts = new List<NormalizedContentPart>
            {
                new()
                {
                    Type = NormalizedPartTypes.ToolCall,
                    ToolCallId = "call-" + index,
                    ToolName = toolName,
                    ToolVersion = effect is null ? null : "1",
                    ToolEffect = effect,
                    ToolDescriptorDigest =
                        effect is null ? null : "descriptor-digest",
                    Json = Parse(arguments)
                }
            }
        };
    }

    private static string Receipt(
        string status,
        long revision,
        string result)
    {
        return $$"""
                 {
                   "status":"{{status}}",
                   "revision":{{revision}},
                   "result":{{result}},
                   "stateDiff":null,
                   "authoritativeObservations":[],
                   "retryable":false,
                   "operationId":"volatile-operation",
                   "receivedAt":"2026-01-01T00:00:00Z"
                 }
                 """;
    }

    private static JsonElement Parse(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
