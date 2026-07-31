using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class RuntimeTraceTests
{
    [Fact]
    public void ExporterRedactsSensitiveNamesValuesAndUrlUserInfo()
    {
        var githubToken = string.Concat(
            "g",
            "hp_",
            "abcdefghijklmnopqrstuvwxyz123456");
        var cloudAccessId = string.Concat(
            "AK",
            "IA",
            "1234567890ABCDEF");
        var jwt = string.Concat(
            "eyJabcdefgh",
            ".ijklmnop",
            ".qrstuvwx");
        var privateKeyMarker = string.Concat(
            "-----BEGIN PRI",
            "VATE KEY-----");
        var bearerCredential = string.Concat(
            "Bear",
            "er ",
            "abcdefghijklmnopqrstuvwxyz123456");
        var fineGrainedToken = string.Concat(
            "github_",
            "pat_",
            "abcdefghijklmnopqrstuvwxyz123456");
        var runtimeEvent = Event(
            "event-0",
            0,
            RuntimeEventKinds.RunStarted,
            $$"""
            {
              "apiKey":"do-not-export",
              "x-api-key":"also-do-not-export",
              "header":"Bearer do-not-export",
              "nested":{"password":"do-not-export"},
              "endpoint":"https://user:pass@example.invalid/path",
              "accessToken":"credential-value",
              "inputTokens":42,
              "estimatedTokens":50,
              "value1":"prefix {{githubToken}} suffix",
              "value2":"{{cloudAccessId}}",
              "value3":"prefix {{jwt}} suffix",
              "value4":"{{privateKeyMarker}}",
              "value5":"prefix {{bearerCredential}} suffix",
              "array":["safe-array-value","{{fineGrainedToken}}"],
              "safe":"visible"
            }
            """);
        runtimeEvent.ProviderId = string.Concat(
            "sk-",
            "top-level-provider-",
            "abcdefghijklmnopqrstuvwxyz");
        runtimeEvent.ModelId =
            "https://user:pass@example.invalid/model";

        var export = new RuntimeTraceExporter().Export(
            new[] { runtimeEvent });

        Assert.Equal(1, export.EventCount);
        Assert.Equal(14, export.RedactedValueCount);
        Assert.DoesNotContain("do-not-export", export.JsonLines);
        Assert.DoesNotContain("also-do-not-export", export.JsonLines);
        Assert.DoesNotContain("credential-value", export.JsonLines);
        Assert.DoesNotContain("ghp_", export.JsonLines);
        Assert.DoesNotContain("AKIA", export.JsonLines);
        Assert.DoesNotContain("eyJabcdefgh", export.JsonLines);
        Assert.DoesNotContain("PRIVATE KEY", export.JsonLines);
        Assert.DoesNotContain("github_pat_", export.JsonLines);
        Assert.DoesNotContain(
            "abcdefghijklmnopqrstuvwxyz123456",
            export.JsonLines);
        Assert.DoesNotContain("user:pass", export.JsonLines);
        Assert.DoesNotContain("top-level-provider", export.JsonLines);
        Assert.Contains("\"inputTokens\":42", export.JsonLines);
        Assert.Contains("\"estimatedTokens\":50", export.JsonLines);
        Assert.Contains("visible", export.JsonLines);
        Assert.Equal(64, export.Digest.Length);
        AssertEveryLineIsValid(export);
    }

    [Fact]
    public void ExporterReplacesSensitiveIdsWithDeterministicValidIds()
    {
        var credential = string.Concat(
            "sk-",
            "live-",
            "abcdefghijklmnopqrstuvwxyz123456");
        var runtimeEvent = Event(
            credential,
            0,
            RuntimeEventKinds.ProviderDispatchStarted);
        runtimeEvent.RunId = credential;
        runtimeEvent.TurnId = credential;
        runtimeEvent.AttemptId = credential;
        runtimeEvent.StreamAttemptId = credential;

        var exporter = new RuntimeTraceExporter();
        var first = exporter.Export(new[] { runtimeEvent });
        var second = exporter.Export(new[] { runtimeEvent });
        var sanitized = ProtocolJson.DeserializeRuntimeEvent(
            first.JsonLines.Trim());

        Assert.Equal(first.JsonLines, second.JsonLines);
        Assert.StartsWith("redacted:sha256:", sanitized.EventId);
        Assert.StartsWith("redacted:sha256:", sanitized.RunId);
        Assert.StartsWith("redacted:sha256:", sanitized.TurnId);
        Assert.StartsWith("redacted:sha256:", sanitized.AttemptId);
        Assert.StartsWith(
            "redacted:sha256:",
            sanitized.StreamAttemptId);
        Assert.Equal(5, first.RedactedValueCount);
        Assert.DoesNotContain(credential, first.JsonLines);
        AssertEveryLineIsValid(first);
    }

    [Fact]
    public void ProjectorFindsTerminalCountsAndAnomalies()
    {
        var events = new[]
        {
            Event("duplicate", 0, RuntimeEventKinds.RunStarted),
            Event("turn", 1, RuntimeEventKinds.TurnStarted),
            Event("duplicate", 3, RuntimeEventKinds.ActionRequested),
            Event("done", 4, RuntimeEventKinds.RunCompleted)
        };

        var projection = new RuntimeJournalProjector().Project(events);

        Assert.Equal(RuntimeEventKinds.RunCompleted, projection.TerminalKind);
        Assert.Equal(1, projection.Turns);
        Assert.Equal(1, projection.ActionRequests);
        Assert.Equal(
            new[]
            {
                "projection_duplicate_event_id",
                "projection_sequence_gap"
            },
            projection.AnomalyCodes);
    }

    [Fact]
    public void ExporterNeverPersistsTranscriptReasoning()
    {
        var message = new NormalizedMessage
        {
            MessageId = "assistant-private",
            Role = NormalizedRoles.Assistant,
            CreatedAt = DateTimeOffset.UnixEpoch,
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromReasoning(
                    "private-reasoning-must-not-export"),
                NormalizedContentPart.FromText("visible answer")
            }
        };
        var runtimeEvent = Event(
            "transcript",
            0,
            RuntimeEventKinds.TranscriptMessage);
        runtimeEvent.Payload = NormalizedMessageJournalCodec.Encode(message);

        var export = new RuntimeTraceExporter().Export(
            new[] { runtimeEvent });

        Assert.DoesNotContain(
            "private-reasoning-must-not-export",
            export.JsonLines);
        Assert.DoesNotContain(
            NormalizedPartTypes.Reasoning,
            export.JsonLines);
        Assert.Contains("visible answer", export.JsonLines);
        Assert.Equal(1, export.RedactedValueCount);
    }

    [Fact]
    public void ExporterRedactsCredentialsUsedAsPropertyNames()
    {
        var credential = string.Concat(
            "sk-",
            "live-",
            "abcdefghijklmnopqrstuvwxyz123456");
        var runtimeEvent = Event(
            "credential-key",
            0,
            RuntimeEventKinds.RunStarted,
            $$"""
            {
              "[REDACTED_KEY_0]":"safe-collision",
              "{{credential}}":true,
              "endpoint":"https://example.invalid/path?api_key=ordinary-value"
            }
            """);
        runtimeEvent.Extensions[credential] =
            ProtocolJson.ParseElement("true");

        var export = new RuntimeTraceExporter().Export(
            new[] { runtimeEvent });

        Assert.DoesNotContain(credential, export.JsonLines);
        Assert.DoesNotContain("api_key", export.JsonLines);
        Assert.Contains("safe-collision", export.JsonLines);
        Assert.Contains("[REDACTED_KEY_1]", export.JsonLines);
        Assert.Equal(3, export.RedactedValueCount);
    }

    [Fact]
    public void ProjectorReportsAMissingInitialJournalEvent()
    {
        var projection = new RuntimeJournalProjector().Project(
            new[]
            {
                Event("turn", 1, RuntimeEventKinds.TurnStarted),
                Event("done", 2, RuntimeEventKinds.RunCompleted)
            });

        Assert.Contains(
            "projection_sequence_gap",
            projection.AnomalyCodes);
    }

    [Fact]
    public void AnalyzerRejectsInitialTranscriptAfterARealTurnStarts()
    {
        var turnStarted = Event(
            "turn-start",
            1,
            RuntimeEventKinds.TurnStarted);
        turnStarted.TurnId = "turn-1";
        turnStarted.AttemptId = "attempt-1";
        var lateInitial = Event(
            "late-initial",
            2,
            RuntimeEventKinds.TranscriptMessage);
        lateInitial.TurnId = "initial";
        lateInitial.Payload = NormalizedMessageJournalCodec.Encode(
            new NormalizedMessage
            {
                MessageId = "late-message",
                Role = NormalizedRoles.User,
                CreatedAt = DateTimeOffset.UnixEpoch,
                Parts = new List<NormalizedContentPart>
                {
                    NormalizedContentPart.FromText("late")
                }
            });

        var analysis = new RuntimeTraceAnalyzer().Analyze(
            new[]
            {
                Event("start", 0, RuntimeEventKinds.RunStarted),
                turnStarted,
                lateInitial
            });

        Assert.Contains(
            "trajectory_turn_scope_not_started",
            analysis.Trajectory.AssertionFailureCodes);
    }

    [Fact]
    public void ScenarioEvaluatorReturnsStableFailureCodes()
    {
        var events = new[]
        {
            Event("start", 0, RuntimeEventKinds.RunStarted),
            Event("turn", 1, RuntimeEventKinds.TurnStarted),
            Event("fail", 2, RuntimeEventKinds.RunFailed)
        };

        var evaluation = new RuntimeScenarioEvaluator().Evaluate(
            events,
            new RuntimeScenarioExpectation
            {
                RequiredEventKinds = new[]
                {
                    RuntimeEventKinds.ProviderDispatchStarted
                },
                ForbiddenEventKinds = new[] { RuntimeEventKinds.RunFailed },
                TerminalKind = RuntimeEventKinds.RunCompleted,
                MaximumTurns = 0
            });

        Assert.False(evaluation.Passed);
        Assert.Equal(
            new[]
            {
                "scenario_forbidden_event_present",
                "scenario_required_event_missing",
                "scenario_terminal_kind_mismatch",
                "scenario_turn_limit_exceeded"
            },
            evaluation.FailureCodes);
    }

    [Fact]
    public void ExportLimitsFailClosed()
    {
        var exporter = new RuntimeTraceExporter(
            new RuntimeTraceExportOptions
            {
                MaxEvents = 1,
                MaxUtf8Bytes = 4_096
            });

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => exporter.Export(
                new[]
                {
                    Event("one", 0, RuntimeEventKinds.RunStarted),
                    Event("two", 1, RuntimeEventKinds.RunCompleted)
                }));

        Assert.Equal("trace_event_count_exceeded", error.LimitCode);
    }

    [Fact]
    public void ExporterPreflightsAggregateTopLevelMetadata()
    {
        var runtimeEvent = Event(
            "event-" + new string('a', 1_400),
            0,
            RuntimeEventKinds.RunStarted);
        runtimeEvent.ProviderId = "provider-" + new string('b', 1_400);
        runtimeEvent.ModelId = "model-" + new string('c', 1_400);
        var exporter = new RuntimeTraceExporter(
            new RuntimeTraceExportOptions
            {
                MaxUtf8Bytes = 4_096
            });

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => exporter.Export(new[] { runtimeEvent }));

        Assert.Equal("trace_event_value_exceeded", error.LimitCode);
    }

    [Fact]
    public void ExporterPreflightsEscapedExtensionKeys()
    {
        var runtimeEvent = Event(
            "event",
            0,
            RuntimeEventKinds.RunStarted);
        runtimeEvent.Extensions[new string('\0', 1_000)] =
            ProtocolJson.ParseElement("true");
        var exporter = new RuntimeTraceExporter(
            new RuntimeTraceExportOptions
            {
                MaxUtf8Bytes = 4_096
            });

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => exporter.Export(new[] { runtimeEvent }));

        Assert.Equal("trace_event_value_exceeded", error.LimitCode);
    }

    [Fact]
    public void ExporterCombinesMetadataAndPayloadBudgets()
    {
        var runtimeEvent = Event(
            "event",
            0,
            RuntimeEventKinds.RunStarted,
            $$"""{"text":"{{new string('p', 2_800)}}"}""");
        runtimeEvent.ProviderId =
            "provider-" + new string('m', 1_400);
        var exporter = new RuntimeTraceExporter(
            new RuntimeTraceExportOptions
            {
                MaxUtf8Bytes = 4_096
            });

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => exporter.Export(new[] { runtimeEvent }));

        Assert.Equal("trace_event_value_exceeded", error.LimitCode);
    }

    private static RuntimeEvent Event(
        string id,
        long sequence,
        string kind,
        string payload = "{}")
    {
        return new RuntimeEvent
        {
            EventId = id,
            RunId = "run",
            Sequence = sequence,
            Kind = kind,
            Durability = EventDurabilities.Durable,
            RuntimeGeneration = 1,
            Timestamp = DateTimeOffset.UnixEpoch,
            Payload = ProtocolJson.ParseElement(payload)
        };
    }

    private static void AssertEveryLineIsValid(RuntimeTraceExport export)
    {
        foreach (var line in export.JsonLines.Split(
                     '\n',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var runtimeEvent =
                ProtocolJson.DeserializeRuntimeEvent(line);
            ProtocolValidator.EnsureValid(runtimeEvent);
        }
    }
}
