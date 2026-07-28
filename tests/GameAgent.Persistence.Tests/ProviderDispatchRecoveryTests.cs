using System.Runtime.CompilerServices;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;

namespace GameAgent.Persistence.Tests;

public sealed class ProviderDispatchRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialOrTamperedProviderRouteIsRejectedAsCorruption(
        bool partial)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-provider-route-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var ids = new Ids();
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                ids);
            var run = CreateRun(clock.UtcNow);
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var dispatch = ProviderEvent(
                run,
                RuntimeEventKinds.ProviderDispatchStarted,
                "provider-1",
                clock.UtcNow);
            dispatch.ModelId = "model-1";
            if (!partial)
            {
                dispatch.TransportDialect = "test.streaming.v1";
                dispatch.ProviderCapabilityDigest = new string('a', 64);
                dispatch.ProviderRouteDigest = new string('b', 64);
            }

            await store.AppendAtomicAsync(dispatch, run.Revision);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => new RunRecovery(store, store, journal)
                    .LoadAsync(run.RunId, default)
                    .AsTask());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MismatchedProviderSettlementIsRejectedAsCorruption()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-provider-identity-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var ids = new Ids();
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                ids);
            var run = CreateRun(clock.UtcNow);
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var dispatchAppend = await store.AppendAtomicAsync(
                ProviderEvent(
                    run,
                    RuntimeEventKinds.ProviderDispatchStarted,
                    "provider-1",
                    clock.UtcNow),
                run.Revision);
            run.Revision = dispatchAppend.Revision;
            await store.AppendAtomicAsync(
                ProviderEvent(
                    run,
                    RuntimeEventKinds.BudgetUpdated,
                    "provider-2",
                    clock.UtcNow),
                run.Revision);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => new RunRecovery(store, store, journal)
                    .LoadAsync(run.RunId, default)
                    .AsTask());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BilledResultMissingFailsClosedWithoutRedispatch(
        bool emitToolCall)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-provider-result-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var crashStore = new RejectAfterProviderUsageStore(store);
            var firstProvider = new SuccessfulProvider(emitToolCall);
            var clock = new Clock();
            var ids = new Ids();
            var run = CreateRun(clock.UtcNow);

            await using (var firstRuntime = CreateRuntime(
                             crashStore,
                             crashStore,
                             firstProvider,
                             clock,
                             ids))
            {
                var interrupted = await firstRuntime.RunAsync(
                    new DurableRunRequest { Run = run });

                Assert.Equal(RunStates.Running, interrupted.Run.State);
                Assert.Equal(1, firstProvider.CallCount);
                Assert.True(crashStore.RejectedPostUsageAppend);
            }

            var beforeRecovery = await store.ReadRunAsync(run.RunId, default);
            var usage = Assert.Single(
                beforeRecovery,
                item => item.Kind == RuntimeEventKinds.BudgetUpdated
                        && !string.IsNullOrWhiteSpace(
                            item.StreamAttemptId));
            Assert.DoesNotContain(
                beforeRecovery,
                item => item.Kind
                        == RuntimeEventKinds.ProviderResultCommitted
                        || item.Kind
                        == RuntimeEventKinds.ProviderResultDiscarded);
            Assert.DoesNotContain(
                beforeRecovery,
                item => item.Kind == RuntimeEventKinds.TranscriptMessage
                        && NormalizedMessageJournalCodec
                            .Decode(item.Payload)
                            .Role == NormalizedRoles.Assistant);

            var secondProvider = new NeverInvokedProvider();
            await using var recoveredRuntime = CreateRuntime(
                store,
                store,
                secondProvider,
                clock,
                ids);

            var recovered = await recoveredRuntime.ResumeAsync(run.RunId);

            Assert.Equal(RunStates.Failed, recovered.Run.State);
            Assert.Equal(
                "provider_result_recovery_required",
                recovered.ErrorCode);
            Assert.Equal("provider", recovered.ErrorCategory);
            Assert.False(recovered.Run.Usage.HasUnaccountedUsage);
            Assert.Equal(0, secondProvider.CallCount);

            var repeated = await recoveredRuntime.ResumeAsync(run.RunId);
            Assert.Equal(RunStates.Failed, repeated.Run.State);
            Assert.Equal(0, secondProvider.CallCount);

            var afterRecovery = await store.ReadRunAsync(run.RunId, default);
            Assert.Single(
                afterRecovery,
                item => item.Kind == RuntimeEventKinds.RunFailed);
            Assert.DoesNotContain(
                afterRecovery,
                item => item.Kind
                        == RuntimeEventKinds.ProviderUsageUncertain);
            Assert.Equal(firstProvider.ProviderId, usage.ProviderId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnsettledDispatchFailsClosedBeforeAnotherProviderCall()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-provider-dispatch-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var crashStore = new RejectAfterDispatchStore(store);
            var firstProvider = new DispatchThenFailProvider();
            var clock = new Clock();
            var ids = new Ids();
            var run = CreateRun(clock.UtcNow);

            await using (var firstRuntime = CreateRuntime(
                             crashStore,
                             crashStore,
                             firstProvider,
                             clock,
                             ids))
            {
                var interrupted = await firstRuntime.RunAsync(
                    new DurableRunRequest { Run = run });

                Assert.Equal(RunStates.Running, interrupted.Run.State);
                Assert.Equal(1, firstProvider.CallCount);
                Assert.True(crashStore.RejectedPostDispatchAppend);
            }

            var beforeRecovery = await store.ReadRunAsync(run.RunId, default);
            var dispatch = Assert.Single(
                beforeRecovery,
                item => item.Kind
                        == RuntimeEventKinds.ProviderDispatchStarted);
            Assert.Equal(firstProvider.ProviderId, dispatch.ProviderId);
            Assert.Equal(
                firstProvider.RouteMetadata.ModelId,
                dispatch.ModelId);
            Assert.Equal(
                firstProvider.RouteMetadata.TransportDialect,
                dispatch.TransportDialect);
            Assert.Equal(64, dispatch.ProviderCapabilityDigest!.Length);
            Assert.Equal(64, dispatch.ProviderRouteDigest!.Length);
            Assert.False(string.IsNullOrWhiteSpace(dispatch.AttemptId));
            Assert.False(string.IsNullOrWhiteSpace(
                dispatch.StreamAttemptId));
            Assert.DoesNotContain(
                beforeRecovery,
                item => item.Kind
                        == RuntimeEventKinds.ProviderDispatchKnownZero
                        || item.Kind
                        == RuntimeEventKinds.ProviderUsageUncertain
                        || item.Kind == RuntimeEventKinds.BudgetUpdated
                           && item.StreamAttemptId
                           == dispatch.StreamAttemptId);

            var secondProvider = new NeverInvokedProvider();
            await using var recoveredRuntime = CreateRuntime(
                store,
                store,
                secondProvider,
                clock,
                ids);

            var recovered = await recoveredRuntime.ResumeAsync(run.RunId);

            Assert.Equal(RunStates.Failed, recovered.Run.State);
            Assert.Equal(
                "provider_usage_reconciliation_required",
                recovered.ErrorCode);
            Assert.Equal("billing", recovered.ErrorCategory);
            Assert.True(recovered.Run.Usage.HasUnaccountedUsage);
            Assert.Equal(
                1,
                recovered.Run.Usage.UnaccountedProviderAttempts);
            Assert.Equal(0, secondProvider.CallCount);

            var afterRecovery = await store.ReadRunAsync(run.RunId, default);
            var uncertain = Assert.Single(
                afterRecovery,
                item => item.Kind
                        == RuntimeEventKinds.ProviderUsageUncertain);
            Assert.Equal(dispatch.ProviderId, uncertain.ProviderId);
            Assert.Equal(dispatch.AttemptId, uncertain.AttemptId);
            Assert.Equal(
                dispatch.StreamAttemptId,
                uncertain.StreamAttemptId);
            Assert.Equal(
                "provider_dispatch_recovery_unknown",
                uncertain.ReasonCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DurableAgentRuntime CreateRuntime(
        IDurableSessionStore store,
        IOperationLedger operations,
        IStreamingModelProvider provider,
        IRuntimeClock clock,
        IRuntimeIdGenerator ids)
    {
        var journal = new JournalCoordinator(
            store,
            operations,
            clock,
            ids);
        return new DurableAgentRuntime(
            new ProviderAttemptRunner(
                new[] { provider },
                new ProviderRetryPolicy
                {
                    MaxAttemptsPerProvider = 1,
                    IdleTimeout = TimeSpan.FromSeconds(2),
                    TotalTimeout = TimeSpan.FromSeconds(5)
                },
                new SystemRuntimeDelay(),
                ids),
            new Host(),
            journal,
            new RunRecovery(store, operations, journal),
            new ToolCatalogRegistry(),
            new SkillCatalogRegistry(),
            new ContextCompiler(),
            new ToolBatchPlanner(),
            new ToolBatchScheduler(),
            clock,
            ids,
            new DurableAgentRuntimeOptions
            {
                ModelId = "test-model",
                MaxConcurrentProviderCalls = 1
            });
    }

    private static AgentRun CreateRun(DateTimeOffset now)
    {
        return new AgentRun
        {
            RunId = "run-" + Guid.NewGuid().ToString("N"),
            AgentId = "agent-1",
            WorldId = "world-1",
            SessionId = "session-1",
            State = RunStates.Queued,
            Budget = new AgentBudget
            {
                MaxTurns = 4,
                MaxDurationMs = 10_000,
                MaxTokens = 2_000,
                MaxCostUsd = "1",
                MaxActions = 0
            },
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static RuntimeEvent ProviderEvent(
        AgentRun run,
        string kind,
        string providerId,
        DateTimeOffset timestamp)
    {
        return new RuntimeEvent
        {
            EventId = kind + "-" + Guid.NewGuid().ToString("N"),
            RunId = run.RunId,
            TurnId = "turn-1",
            Kind = kind,
            Durability = EventDurabilities.Durable,
            RuntimeGeneration = run.RuntimeGeneration,
            AttemptId = "provider-attempt-1",
            StreamAttemptId = "stream-attempt-1",
            ProviderId = providerId,
            Timestamp = timestamp,
            Payload = ProtocolJson.ToElement(run)
        };
    }

    private sealed class DispatchThenFailProvider :
        IStreamingModelProvider,
        IProviderRouteMetadataSource
    {
        private int _callCount;

        public string ProviderId => "dispatch-then-fail";

        public ProviderRouteMetadata RouteMetadata { get; } =
            new("dispatch-model", "test.dispatch.v1");

        public int CallCount => Volatile.Read(ref _callCount);

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 100_000
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            await Task.Yield();
            throw new IOException("Simulated process loss after dispatch.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class NeverInvokedProvider : IStreamingModelProvider
    {
        private int _callCount;

        public string ProviderId => "never-invoked";

        public int CallCount => Volatile.Read(ref _callCount);

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 100_000
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Interlocked.Increment(ref _callCount);
            await Task.Yield();
            throw new InvalidOperationException(
                "Recovery must not dispatch another provider request.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class SuccessfulProvider : IStreamingModelProvider
    {
        private readonly bool _emitToolCall;
        private int _callCount;

        public SuccessfulProvider(bool emitToolCall)
        {
            _emitToolCall = emitToolCall;
        }

        public string ProviderId => "successful-provider";

        public int CallCount => Volatile.Read(ref _callCount);

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 100_000
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            await Task.Yield();
            if (_emitToolCall)
            {
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.ToolCallDelta,
                    ToolCallId = "tool-call-1",
                    ToolNameDelta = "unknown_tool",
                    ArgumentsJsonDelta = "{}"
                };
            }
            else
            {
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.TextDelta,
                    TextDelta = """{"value":"done"}"""
                };
            }

            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 1,
                Kind = ModelStreamEventKinds.Usage,
                Usage = new ProviderUsage
                {
                    InputTokens = 3,
                    OutputTokens = 2,
                    CostUsd = "0.001"
                }
            };
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 2,
                Kind = ModelStreamEventKinds.Completed,
                FinishReason = _emitToolCall ? "tool_calls" : "stop"
            };
        }
    }

    private sealed class Host : IGameHost
    {
        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            throw new InvalidOperationException("No tool call expected.");
        }
    }

    private sealed class Clock : IRuntimeClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class Ids : IRuntimeIdGenerator
    {
        public string NewId(string category)
        {
            return category + "-" + Guid.NewGuid().ToString("N");
        }
    }

    private sealed class RejectAfterDispatchStore :
        IDurableSessionStore,
        IOperationLedger
    {
        private readonly FileSessionStore _inner;
        private int _dispatchCommitted;

        public RejectAfterDispatchStore(FileSessionStore inner)
        {
            _inner = inner;
        }

        public bool RejectedPostDispatchAppend { get; private set; }

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            return _inner.AppendAsync(runtimeEvent, cancellationToken);
        }

        public async ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            RejectIfDispatchWasCommitted();
            var result = await _inner.AppendAtomicAsync(
                    runtimeEvent,
                    expectedRunRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (runtimeEvent.Kind
                == RuntimeEventKinds.ProviderDispatchStarted)
            {
                Volatile.Write(ref _dispatchCommitted, 1);
            }

            return result;
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            RejectIfDispatchWasCommitted();
            return _inner.AppendAtomicBatchAsync(
                runtimeEvents,
                expectedRunRevision,
                cancellationToken);
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            return _inner.ReadRunAsync(runId, cancellationToken);
        }

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            return _inner.GetRunCursorAsync(runId, cancellationToken);
        }

        public ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            return _inner.FlushAsync(cancellationToken);
        }

        public ValueTask<OperationLedgerEntry?> GetOperationAsync(
            string operationId,
            CancellationToken cancellationToken = default)
        {
            return _inner.GetOperationAsync(operationId, cancellationToken);
        }

        public ValueTask<IReadOnlyList<OperationLedgerEntry>>
            ReadPendingOperationsAsync(
                string? runId = null,
                CancellationToken cancellationToken = default)
        {
            return _inner.ReadPendingOperationsAsync(runId, cancellationToken);
        }

        public ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
            RuntimeEvent receiptEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            return _inner.ReconcileReceiptAsync(
                receiptEvent,
                expectedRunRevision,
                cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return default;
        }

        private void RejectIfDispatchWasCommitted()
        {
            if (Volatile.Read(ref _dispatchCommitted) == 0)
            {
                return;
            }

            RejectedPostDispatchAppend = true;
            throw new IOException(
                "Simulated process loss after provider dispatch.");
        }
    }

    private sealed class RejectAfterProviderUsageStore :
        IDurableSessionStore,
        IOperationLedger
    {
        private readonly FileSessionStore _inner;
        private int _usageCommitted;

        public RejectAfterProviderUsageStore(FileSessionStore inner)
        {
            _inner = inner;
        }

        public bool RejectedPostUsageAppend { get; private set; }

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            return _inner.AppendAsync(runtimeEvent, cancellationToken);
        }

        public async ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            RejectIfUsageWasCommitted();
            var result = await _inner.AppendAtomicAsync(
                    runtimeEvent,
                    expectedRunRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (runtimeEvent.Kind == RuntimeEventKinds.BudgetUpdated
                && !string.IsNullOrWhiteSpace(
                    runtimeEvent.StreamAttemptId))
            {
                Volatile.Write(ref _usageCommitted, 1);
            }

            return result;
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            RejectIfUsageWasCommitted();
            return _inner.AppendAtomicBatchAsync(
                runtimeEvents,
                expectedRunRevision,
                cancellationToken);
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            return _inner.ReadRunAsync(runId, cancellationToken);
        }

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            return _inner.GetRunCursorAsync(runId, cancellationToken);
        }

        public ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            return _inner.FlushAsync(cancellationToken);
        }

        public ValueTask<OperationLedgerEntry?> GetOperationAsync(
            string operationId,
            CancellationToken cancellationToken = default)
        {
            return _inner.GetOperationAsync(operationId, cancellationToken);
        }

        public ValueTask<IReadOnlyList<OperationLedgerEntry>>
            ReadPendingOperationsAsync(
                string? runId = null,
                CancellationToken cancellationToken = default)
        {
            return _inner.ReadPendingOperationsAsync(runId, cancellationToken);
        }

        public ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
            RuntimeEvent receiptEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            return _inner.ReconcileReceiptAsync(
                receiptEvent,
                expectedRunRevision,
                cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return default;
        }

        private void RejectIfUsageWasCommitted()
        {
            if (Volatile.Read(ref _usageCommitted) == 0)
            {
                return;
            }

            RejectedPostUsageAppend = true;
            throw new IOException(
                "Simulated process loss after provider usage.");
        }
    }
}
