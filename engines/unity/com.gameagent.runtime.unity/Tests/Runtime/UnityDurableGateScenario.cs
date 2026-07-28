using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;
using UnityEngine;
using UnityEngine.Scripting;

namespace GameAgent.Unity.Tests
{
    [Preserve]
    public sealed class UnityDurableGateResult
    {
        public string RunId { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public int ProviderCalls { get; set; }

        public int ActionCalls { get; set; }

        public bool ActionRanOnMainThread { get; set; }

        public bool ActionRequestedJournaled { get; set; }

        public bool ActionReceivedJournaled { get; set; }

        public bool ProtocolRoundTripCompleted { get; set; }

        public bool StructuredContextObserved { get; set; }

        public bool ToolResultFeedbackObserved { get; set; }

        public bool TranscriptContainsToolResult { get; set; }

        public bool Passed
        {
            get
            {
                return string.Equals(
                           State,
                           RunStates.Completed,
                           StringComparison.Ordinal)
                       && ProviderCalls == 2
                       && ActionCalls == 1
                       && ActionRanOnMainThread
                       && ActionRequestedJournaled
                       && ActionReceivedJournaled
                       && ProtocolRoundTripCompleted
                       && StructuredContextObserved
                       && ToolResultFeedbackObserved
                       && TranscriptContainsToolResult;
            }
        }
    }

    [Preserve]
    public static class UnityDurableGateScenario
    {
        public const string MarkerSchema =
            "game-agent-unity-durable-gate/v1";

        public const string RunId = "unity-durable-gate-run";

        private const string ToolName = "apply_world_command";

        public static async Task<UnityDurableGateResult> RunAsync(
            UnityAgentRuntimeHost host,
            string journalPath,
            CancellationToken cancellationToken)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }
            if (host.IsConfigured)
            {
                throw new InvalidOperationException(
                    "The Unity durable gate requires an unconfigured host.");
            }
            if (string.IsNullOrWhiteSpace(journalPath))
            {
                throw new ArgumentException(
                    "A journal path is required.",
                    nameof(journalPath));
            }

            var mainThreadId = Environment.CurrentManagedThreadId;
            var actionThreadId = 0;
            var actionCalls = 0;
            var protocolRoundTripCompleted = false;
            var clock = new GateClock();
            var ids = new GateIdGenerator();
            var provider = new GateStreamingProvider();
            var store = new FileSessionStore(journalPath);
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                ids);
            var tools = new ToolCatalogRegistry();
            tools.Replace(new[] { CreateTool() });
            var runtime = new DurableAgentRuntime(
                new ProviderAttemptRunner(
                    new IStreamingModelProvider[] { provider },
                    new ProviderRetryPolicy
                    {
                        MaxAttemptsPerProvider = 1,
                        IdleTimeout = TimeSpan.FromSeconds(2),
                        TotalTimeout = TimeSpan.FromSeconds(5)
                    },
                    new SystemRuntimeDelay(),
                    ids),
                new UnityMainThreadGameHost(
                    host.Dispatcher,
                    (request, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        actionThreadId =
                            Environment.CurrentManagedThreadId;
                        Interlocked.Increment(ref actionCalls);
                        ValidateActionRequest(request);

                        var requestJson =
                            UnityProtocolBridge.ToJson(request);
                        var requestRoundTrip =
                            UnityProtocolBridge.ActionRequestFromJson(
                                requestJson);
                        var unityRequest =
                            UnityProtocolBridge.ToUnity(
                                requestRoundTrip);
                        if (!string.Equals(
                                unityRequest.actionName,
                                ToolName,
                                StringComparison.Ordinal)
                            || unityRequest.argumentsJson.IndexOf(
                                "\"opcode\":7",
                                StringComparison.Ordinal) < 0)
                        {
                            throw new InvalidOperationException(
                                "The Unity action bridge changed the "
                                + "structured command.");
                        }

                        var receipt = new ActionReceipt
                        {
                            OperationId = request.OperationId,
                            Revision = 0,
                            Status = ReceiptStatuses.Succeeded,
                            Result = ProtocolJson.ParseElement(
                                "{\"applied\":true,\"revision\":11}"),
                            ReceivedAt = clock.UtcNow,
                            CommittedAt = clock.UtcNow
                        };
                        var receiptRoundTrip =
                            UnityProtocolBridge.ActionReceiptFromJson(
                                UnityProtocolBridge.ToJson(receipt));
                        protocolRoundTripCompleted = true;
                        return new ValueTask<ActionReceipt>(
                            receiptRoundTrip);
                    },
                    clock),
                journal,
                new RunRecovery(store, store, journal),
                tools,
                new SkillCatalogRegistry(),
                new ContextCompiler(),
                new ToolBatchPlanner(),
                new ToolBatchScheduler(),
                clock,
                ids,
                new DurableAgentRuntimeOptions
                {
                    ModelId = "unity-deterministic-gate",
                    MaxConcurrentProviderCalls = 1
                });

            host.Configure(
                runtime,
                store,
                ownsSessionStore: true,
                ownsRuntime: true);

            var outcome = await host.RunAsync(
                    CreateRunRequest(clock),
                    cancellationToken)
                .ConfigureAwait(false);
            var events = await store.ReadRunAsync(
                    RunId,
                    cancellationToken)
                .ConfigureAwait(false);

            var actionRequested = false;
            var actionReceived = false;
            foreach (var runtimeEvent in events)
            {
                actionRequested |= string.Equals(
                    runtimeEvent.Kind,
                    RuntimeEventKinds.ActionRequested,
                    StringComparison.Ordinal);
                actionReceived |= string.Equals(
                    runtimeEvent.Kind,
                    RuntimeEventKinds.ActionReceived,
                    StringComparison.Ordinal);
            }

            var transcriptContainsToolResult = false;
            foreach (var message in outcome.Transcript)
            {
                foreach (var part in message.Parts)
                {
                    transcriptContainsToolResult |= string.Equals(
                        part.Type,
                        NormalizedPartTypes.ToolResult,
                        StringComparison.Ordinal);
                }
            }

            var result = new UnityDurableGateResult
            {
                RunId = outcome.Run.RunId,
                State = outcome.Run.State,
                ProviderCalls = provider.CallCount,
                ActionCalls = Volatile.Read(ref actionCalls),
                ActionRanOnMainThread =
                    actionThreadId == mainThreadId,
                ActionRequestedJournaled = actionRequested,
                ActionReceivedJournaled = actionReceived,
                ProtocolRoundTripCompleted =
                    protocolRoundTripCompleted,
                StructuredContextObserved =
                    provider.StructuredContextObserved,
                ToolResultFeedbackObserved =
                    provider.ToolResultFeedbackObserved,
                TranscriptContainsToolResult =
                    transcriptContainsToolResult
            };

            if (!result.Passed
                || !outcome.FinalOutput.HasValue
                || !string.Equals(
                    outcome.FinalOutput.Value
                        .GetProperty("decision")
                        .GetString(),
                    "commit",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The deterministic Unity durable tool loop did not "
                    + "satisfy its completion contract. state="
                    + result.State
                    + ", providerCalls=" + result.ProviderCalls
                    + ", actionCalls=" + result.ActionCalls
                    + ", mainThread="
                    + result.ActionRanOnMainThread
                    + ", requested="
                    + result.ActionRequestedJournaled
                    + ", received="
                    + result.ActionReceivedJournaled
                    + ", protocol="
                    + result.ProtocolRoundTripCompleted
                    + ", context="
                    + result.StructuredContextObserved
                    + ", feedback="
                    + result.ToolResultFeedbackObserved
                    + ", transcript="
                    + result.TranscriptContainsToolResult
                    + ", final="
                    + (outcome.FinalOutput.HasValue
                        ? outcome.FinalOutput.Value.GetRawText()
                        : "null"));
            }

            return result;
        }

        public static void WritePassMarker(
            string markerPath,
            string backend,
            UnityDurableGateResult result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }
            if (!result.Passed)
            {
                throw new InvalidOperationException(
                    "A pass marker cannot be written for a failed gate.");
            }

            WriteMarker(
                markerPath,
                JsonArrayBuilder.Object(
                        ("schema", JsonArrayBuilder.String(
                            MarkerSchema)),
                        ("status", JsonArrayBuilder.String("passed")),
                        ("backend", JsonArrayBuilder.String(
                            backend ?? string.Empty)),
                        ("runId", JsonArrayBuilder.String(result.RunId)),
                        ("state", JsonArrayBuilder.String(result.State)),
                        ("providerCalls", JsonArrayBuilder.Number(
                            result.ProviderCalls)),
                        ("actionCalls", JsonArrayBuilder.Number(
                            result.ActionCalls)),
                        ("mainThreadReceipt", BooleanElement(
                            result.ActionRanOnMainThread)),
                        ("actionRequested", BooleanElement(
                            result.ActionRequestedJournaled)),
                        ("actionReceived", BooleanElement(
                            result.ActionReceivedJournaled)),
                        ("protocolRoundTrip", BooleanElement(
                            result.ProtocolRoundTripCompleted)),
                        ("structuredContext", BooleanElement(
                            result.StructuredContextObserved)),
                        ("toolResultFeedback", BooleanElement(
                            result.ToolResultFeedbackObserved)),
                        ("transcriptToolResult", BooleanElement(
                            result.TranscriptContainsToolResult)))
                    .GetRawText());
        }

        public static void WriteFailureMarker(
            string markerPath,
            string backend,
            Exception exception)
        {
            if (exception == null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            WriteMarker(
                markerPath,
                JsonArrayBuilder.Object(
                        ("schema", JsonArrayBuilder.String(
                            MarkerSchema)),
                        ("status", JsonArrayBuilder.String("failed")),
                        ("backend", JsonArrayBuilder.String(
                            backend ?? string.Empty)),
                        ("errorType", JsonArrayBuilder.String(
                            exception.GetType().FullName
                            ?? exception.GetType().Name)))
                    .GetRawText());
        }

        private static ToolDescriptor CreateTool()
        {
            return new ToolDescriptor
            {
                Name = ToolName,
                Version = "1",
                Description =
                    "Apply a deterministic structured world command.",
                ParametersSchema = ProtocolJson.ParseElement(
                    "{\"type\":\"object\","
                    + "\"required\":[\"opcode\",\"entity\",\"value\"],"
                    + "\"properties\":{"
                    + "\"opcode\":{\"type\":\"integer\"},"
                    + "\"entity\":{\"type\":\"integer\"},"
                    + "\"value\":{\"type\":\"number\"}},"
                    + "\"additionalProperties\":false}"),
                Effect = ToolEffects.WorldCommand,
                ConflictScopes = new List<string>
                {
                    "world:entity:42"
                },
                ThreadAffinity = ThreadAffinities.EngineMainThread,
                TimeoutMs = 1000,
                RetryPolicy = "idempotent",
                IdempotencyPolicy = "required"
            };
        }

        private static DurableRunRequest CreateRunRequest(
            IRuntimeClock clock)
        {
            return new DurableRunRequest
            {
                Run = new AgentRun
                {
                    RunId = RunId,
                    AgentId = "unity-gate-agent",
                    WorldId = "unity-gate-world",
                    SessionId = "unity-gate-session",
                    State = RunStates.Queued,
                    RuntimeGeneration = 1,
                    Budget = new AgentBudget
                    {
                        MaxTurns = 4,
                        MaxActions = 2,
                        MaxDurationMs = 5000,
                        MaxTokens = 2000,
                        MaxCostUsd = "0.10"
                    },
                    CreatedAt = clock.UtcNow,
                    UpdatedAt = clock.UtcNow
                },
                Context = new[]
                {
                    new ContextCandidate(
                        "unity-gate-structured-input",
                        "engine_command_buffer",
                        ProtocolJson.ParseElement(
                            "{\"opcode\":7,\"entity\":42,"
                            + "\"value\":0.75,\"flags\":[1,4]}"),
                        priority: 100,
                        required: true,
                        canDefer: false)
                }
            };
        }

        private static void ValidateActionRequest(
            ActionRequest request)
        {
            if (!string.Equals(
                    request.ActionName,
                    ToolName,
                    StringComparison.Ordinal)
                || request.Arguments.GetProperty("opcode")
                    .GetInt32() != 7
                || request.Arguments.GetProperty("entity")
                    .GetInt32() != 42)
            {
                throw new InvalidOperationException(
                    "The host received an unexpected structured action.");
            }
        }

        private static System.Text.Json.JsonElement BooleanElement(
            bool value)
        {
            return ProtocolJson.ParseElement(value ? "true" : "false");
        }

        private static void WriteMarker(
            string markerPath,
            string json)
        {
            if (string.IsNullOrWhiteSpace(markerPath))
            {
                throw new ArgumentException(
                    "A marker path is required.",
                    nameof(markerPath));
            }

            var fullPath = Path.GetFullPath(markerPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                fullPath,
                json,
                new UTF8Encoding(false));
        }

        [Preserve]
        private sealed class GateStreamingProvider
            : IStreamingModelProvider
        {
            private int _callCount;
            private int _structuredContextObserved;
            private int _toolResultFeedbackObserved;

            public string ProviderId
            {
                get { return "unity-deterministic-gate"; }
            }

            public int CallCount
            {
                get { return Volatile.Read(ref _callCount); }
            }

            public bool StructuredContextObserved
            {
                get
                {
                    return Volatile.Read(
                               ref _structuredContextObserved) != 0;
                }
            }

            public bool ToolResultFeedbackObserved
            {
                get
                {
                    return Volatile.Read(
                               ref _toolResultFeedbackObserved) != 0;
                }
            }

            public ProviderCapabilities Capabilities { get; } =
                new ProviderCapabilities
                {
                    Streaming = true,
                    ToolCalling = true,
                    JsonOutput = true,
                    MaxContextTokens = 16_000
                };

            public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
                StreamingModelRequest request,
                [EnumeratorCancellation]
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var call = Interlocked.Increment(ref _callCount);
                ObserveRequest(request);
                if (call == 1)
                {
                    yield return new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 0,
                        Kind = ModelStreamEventKinds.ToolCallDelta,
                        ToolCallId = "unity-gate-tool-call",
                        ToolNameDelta = ToolName,
                        ArgumentsJsonDelta =
                            "{\"opcode\":7,\"entity\":42,"
                            + "\"value\":0.75}"
                    };
                    yield return new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 1,
                        Kind = ModelStreamEventKinds.Usage,
                        Usage = new ProviderUsage
                        {
                            InputTokens = 0,
                            OutputTokens = 0,
                            CostUsd = "0"
                        }
                    };
                    yield return new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 2,
                        Kind = ModelStreamEventKinds.Completed,
                        FinishReason = "tool_calls"
                    };
                    yield break;
                }

                if (call != 2 || !ToolResultFeedbackObserved)
                {
                    throw new InvalidOperationException(
                        "The provider did not receive exactly one "
                        + "journaled tool result before the final turn.");
                }

                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.TextDelta,
                    TextDelta =
                        "{\"decision\":\"commit\",\"revision\":11}"
                };
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 1,
                    Kind = ModelStreamEventKinds.Usage,
                    Usage = new ProviderUsage
                    {
                        InputTokens = 24,
                        OutputTokens = 8,
                        CostUsd = "0"
                    }
                };
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 2,
                    Kind = ModelStreamEventKinds.Completed,
                    FinishReason = "stop"
                };
                await Task.Yield();
            }

            private void ObserveRequest(StreamingModelRequest request)
            {
                foreach (var message in request.Messages)
                {
                    foreach (var part in message.Parts)
                    {
                        if (part.Json.HasValue
                            && part.Json.Value.GetRawText().IndexOf(
                                "\"opcode\":7",
                                StringComparison.Ordinal) >= 0)
                        {
                            Volatile.Write(
                                ref _structuredContextObserved,
                                1);
                        }
                        if (string.Equals(
                                part.Type,
                                NormalizedPartTypes.ToolResult,
                                StringComparison.Ordinal)
                            && string.Equals(
                                part.ToolCallId,
                                "unity-gate-tool-call",
                                StringComparison.Ordinal))
                        {
                            Volatile.Write(
                                ref _toolResultFeedbackObserved,
                                1);
                        }
                    }
                }
            }
        }

        private sealed class GateClock : IRuntimeClock
        {
            public DateTimeOffset UtcNow { get; } =
                new DateTimeOffset(
                    2026,
                    7,
                    28,
                    0,
                    0,
                    0,
                    TimeSpan.Zero);
        }

        private sealed class GateIdGenerator : IRuntimeIdGenerator
        {
            private readonly object _sync = new object();
            private readonly Dictionary<string, int> _sequences =
                new Dictionary<string, int>(StringComparer.Ordinal);

            public string NewId(string category)
            {
                lock (_sync)
                {
                    int sequence;
                    _sequences.TryGetValue(category, out sequence);
                    sequence++;
                    _sequences[category] = sequence;
                    return category + "-" + sequence.ToString("D4");
                }
            }
        }
    }

    [Preserve]
    [DefaultExecutionOrder(-31000)]
    public sealed class UnityDurablePlayerGateBootstrap : MonoBehaviour
    {
        private async void Start()
        {
            var markerPath = ReadArgument("-gameAgentGateMarker");
            if (string.IsNullOrWhiteSpace(markerPath))
            {
                markerPath = Path.Combine(
                    Application.persistentDataPath,
                    "game-agent-unity-gate.pass.json");
            }

            var backend = ReadArgument("-gameAgentGateBackend");
            if (string.IsNullOrWhiteSpace(backend))
            {
                backend = "Unknown";
            }

            var journalPath = markerPath + ".journal";
            UnityAgentRuntimeHost host = null;
            try
            {
                DeleteIfPresent(markerPath);
                DeleteIfPresent(journalPath);
                host = UnityAgentRuntimeHost.EnsureCreated();
                var result = await UnityDurableGateScenario.RunAsync(
                    host,
                    journalPath,
                    CancellationToken.None);
                await host.ShutdownAsync(CancellationToken.None);
                UnityDurableGateScenario.WritePassMarker(
                    markerPath,
                    backend,
                    result);
                Debug.Log(
                    "GAME_AGENT_UNITY_DURABLE_GATE_PASS "
                    + backend);
                Application.Quit(0);
            }
            catch (Exception exception)
            {
                if (host != null)
                {
                    try
                    {
                        await host.ShutdownAsync(
                            CancellationToken.None);
                    }
                    catch (Exception shutdownException)
                    {
                        Debug.LogException(shutdownException);
                    }
                }

                try
                {
                    UnityDurableGateScenario.WriteFailureMarker(
                        markerPath,
                        backend,
                        exception);
                }
                catch (Exception markerException)
                {
                    Debug.LogException(markerException);
                }

                Debug.LogException(exception);
                Application.Quit(1);
            }
        }

        private static string ReadArgument(string name)
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index < arguments.Length - 1; index++)
            {
                if (string.Equals(
                    arguments[index],
                    name,
                    StringComparison.Ordinal))
                {
                    return arguments[index + 1];
                }
            }

            return null;
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
