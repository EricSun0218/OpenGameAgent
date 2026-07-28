using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;
using UnityEngine;

namespace GameAgent.Unity.Samples
{
    public sealed class StructuredToolLoopSample : MonoBehaviour
    {
        private UnityAgentRuntimeHost _host;
        private bool _canRun;

        private void Awake()
        {
            _host = UnityAgentRuntimeHost.EnsureCreated();
            if (_host.IsConfigured)
            {
                Debug.Log(
                    "The Game Agent host is already configured; "
                    + "the durable sample will not replace it.");
                return;
            }

            var clock = new SystemRuntimeClock();
            var ids = new GuidRuntimeIdGenerator();
            var journalPath = Path.Combine(
                Application.persistentDataPath,
                "game-agent-sample.journal");
            var store = new FileSessionStore(journalPath);
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                ids);
            var tools = new ToolCatalogRegistry();
            tools.Replace(new[] { CreateGatherFoodTool() });
            var runtime = new DurableAgentRuntime(
                new ProviderAttemptRunner(
                    new IStreamingModelProvider[]
                    {
                        new SampleStreamingProvider()
                    },
                    new ProviderRetryPolicy
                    {
                        MaxAttemptsPerProvider = 1,
                        IdleTimeout = TimeSpan.FromSeconds(2),
                        TotalTimeout = TimeSpan.FromSeconds(5)
                    },
                    new SystemRuntimeDelay(),
                    ids),
                new UnityMainThreadGameHost(
                    _host.Dispatcher,
                    HandleActionAsync),
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
                    ModelId = "deterministic-sample",
                    MaxConcurrentProviderCalls = 1
                });

            _host.Configure(
                runtime,
                store,
                ownsSessionStore: true,
                ownsRuntime: true);
            _canRun = true;
        }

        private void Start()
        {
            if (_canRun)
            {
                _ = RunSampleAsync();
            }
        }

        private async Task RunSampleAsync()
        {
            try
            {
                var outcome = await _host.RunAsync(
                    CreateRequest(),
                    CancellationToken.None);
                Debug.Log(
                    "Game Agent durable final JSON: "
                    + (outcome.FinalOutput.HasValue
                        ? outcome.FinalOutput.Value.GetRawText()
                        : "null"));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        private static ToolDescriptor CreateGatherFoodTool()
        {
            return new ToolDescriptor
            {
                Name = "gather_food",
                Version = "1",
                Description = "Gather a visible food resource.",
                ParametersSchema = ProtocolJson.ParseElement(
                    "{\"type\":\"object\","
                    + "\"required\":[\"resource\"],"
                    + "\"properties\":{\"resource\":"
                    + "{\"type\":\"string\"}},"
                    + "\"additionalProperties\":false}"),
                Effect = ToolEffects.WorldCommand,
                ConflictScopes = new List<string> { "inventory:player" },
                ThreadAffinity = ThreadAffinities.EngineMainThread,
                TimeoutMs = 1000,
                RetryPolicy = "idempotent",
                IdempotencyPolicy = "required"
            };
        }

        private static ValueTask<ActionReceipt> HandleActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Debug.Log(
                "Unity main-thread action: "
                + request.ActionName + " "
                + request.Arguments.GetRawText());
            return new ValueTask<ActionReceipt>(new ActionReceipt
            {
                OperationId = request.OperationId,
                Revision = 0,
                Status = ReceiptStatuses.Succeeded,
                Result = ProtocolJson.ParseElement(
                    "{\"resource\":\"berries\",\"gathered\":1}"),
                CommittedAt = DateTimeOffset.UtcNow,
                ReceivedAt = DateTimeOffset.UtcNow
            });
        }

        private static DurableRunRequest CreateRequest()
        {
            var now = DateTimeOffset.UtcNow;
            return new DurableRunRequest
            {
                Run = new AgentRun
                {
                    RunId = "sample-" + Guid.NewGuid().ToString("N"),
                    AgentId = "sample-agent",
                    WorldId = "sample-world",
                    SessionId = "sample-session",
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
                    CreatedAt = now,
                    UpdatedAt = now
                },
                Context = new[]
                {
                    new ContextCandidate(
                        "sample-world-state",
                        "world_state",
                        ProtocolJson.ParseElement(
                            "{\"hunger\":70,"
                            + "\"visibleResources\":[\"berries\"]}"),
                        priority: 100,
                        required: true,
                        canDefer: false)
                }
            };
        }

        private sealed class SampleStreamingProvider
            : IStreamingModelProvider
        {
            private int _callCount;

            public string ProviderId
            {
                get { return "deterministic-sample"; }
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
                if (Interlocked.Increment(ref _callCount) == 1)
                {
                    yield return new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = 0,
                        Kind = ModelStreamEventKinds.ToolCallDelta,
                        ToolCallId = "sample-call",
                        ToolNameDelta = "gather_food",
                        ArgumentsJsonDelta =
                            "{\"resource\":\"berries\"}"
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

                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.TextDelta,
                    TextDelta =
                        "{\"decision\":\"eat\","
                        + "\"resource\":\"berries\"}"
                };
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 1,
                    Kind = ModelStreamEventKinds.Usage,
                    Usage = new ProviderUsage
                    {
                        InputTokens = 20,
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
        }
    }
}
