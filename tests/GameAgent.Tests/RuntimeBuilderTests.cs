using System.Runtime.CompilerServices;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;
using GameAgent.Runtime;

namespace GameAgent.Tests;

public sealed class RuntimeBuilderTests
{
    [Fact]
    public async Task BuilderCreatesRunnableOwnedRuntimeAndReleasesJournal()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "runtime.journal");
        try
        {
            await using (var built = new GameAgentRuntimeBuilder(
                                 new RejectingHost())
                             .UseFileJournal(path)
                             .AddProvider(new FinalProvider())
                             .Build())
            {
                var now = DateTimeOffset.UtcNow;
                var outcome = await built.Runtime.RunAsync(
                    new DurableRunRequest
                    {
                        Run = new AgentRun
                        {
                            RunId = "builder-run",
                            AgentId = "agent-1",
                            WorldId = "world-1",
                            State = RunStates.Queued,
                            CreatedAt = now,
                            UpdatedAt = now
                        }
                    });

                Assert.Equal(RunStates.Completed, outcome.Run.State);
                Assert.Equal("ok", outcome.FinalOutput!.Value.GetString());
            }

            using var exclusive = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            Assert.True(exclusive.Length > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BuilderInjectsCustomSkillAdmissionPolicy()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "runtime.journal");
        var policy = new DenyingSkillPolicy();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(
                    new RejectingHost())
                .UseFileJournal(path)
                .AddProvider(new FinalProvider())
                .WithSkills(
                    new[]
                    {
                        new SkillManifest
                        {
                            SkillId = "builder-skill",
                            Version = "1.0.0",
                            Digest = "declared:builder-skill",
                            Description = "Builder injection test.",
                            PromptFragments = new List<string>
                            {
                                "This prompt must not be disclosed."
                            },
                            CapabilityRequirements =
                                ProtocolJson.ParseElement("{}"),
                            ActivationPolicy =
                                ProtocolJson.ParseElement("{}"),
                            Trust = "trusted"
                        }
                    })
                .WithSkillAdmissionPolicy(policy)
                .Build();
            var now = DateTimeOffset.UtcNow;

            var outcome = await built.Runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = new AgentRun
                    {
                        RunId = "builder-skill-run",
                        AgentId = "agent-1",
                        WorldId = "world-1",
                        State = RunStates.Queued,
                        CreatedAt = now,
                        UpdatedAt = now
                    },
                    ActiveSkills = new[]
                    {
                        new SkillReference("builder-skill", "1.0.0")
                    }
                });

            Assert.Equal(RunStates.Failed, outcome.Run.State);
            Assert.Equal("game_skill_denied", outcome.ErrorCode);
            Assert.Equal(1, policy.ActivationCalls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailedBuildReleasesOwnedJournalAfterAsyncCleanup()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "runtime.journal");
        try
        {
            var builder = new GameAgentRuntimeBuilder(new RejectingHost())
                .UseFileJournal(path);

            Assert.Throws<InvalidOperationException>(() => builder.Build());
            await builder.DisposeAsync();

            using var exclusive = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BuilderCleanupNeverSynchronouslyWaitsForAsyncStore()
    {
        var store = new ShutdownTrackingStore(blockDispose: true);
        var builder = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true);

        Assert.False((object)builder is IDisposable);
        var cleanup = builder.DisposeAsync().AsTask();
        await store.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(cleanup.IsCompleted);
        Assert.False(store.WasDisposed);

        store.ReleaseDispose();
        await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(store.WasDisposed);
    }

    [Fact]
    public async Task ShutdownDisposesOwnedStoreWhenFlushFails()
    {
        var store = new ShutdownTrackingStore(
            flushException: new IOException("flush failed"));
        var built = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(new FinalProvider())
            .Build();

        var error = await Assert.ThrowsAsync<IOException>(
            () => built.DisposeAsync().AsTask());

        Assert.Equal("flush failed", error.Message);
        Assert.True(store.WasDisposed);
    }

    [Fact]
    public async Task ShutdownCancellationDoesNotAbortSharedCleanup()
    {
        var store = new ShutdownTrackingStore(blockFlush: true);
        var built = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(new FinalProvider())
            .Build();
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => built.StopAsync(cancellation.Token).AsTask());

        Assert.False(store.WasDisposed);
        store.ReleaseFlush();
        await built.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(store.WasDisposed);
    }

    [Fact]
    public async Task ShutdownCancelsAndDrainsActiveRunBeforeDisposingStore()
    {
        var store = new ShutdownTrackingStore();
        var provider = new CancellableProvider();
        var built = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(provider)
            .Build();
        var now = DateTimeOffset.UtcNow;
        var run = built.Runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = new AgentRun
                    {
                        RunId = "shutdown-run",
                        AgentId = "agent-1",
                        WorldId = "world-1",
                        State = RunStates.Queued,
                        CreatedAt = now,
                        UpdatedAt = now
                    }
                })
            .AsTask();
        var startedOrCompleted = await Task.WhenAny(
            provider.Started.Task,
            run,
            Task.Delay(TimeSpan.FromSeconds(2)));
        if (ReferenceEquals(startedOrCompleted, run))
        {
            var premature = await run;
            throw new Xunit.Sdk.XunitException(
                $"Run ended before the provider started: "
                + $"state={premature.Run.State}, "
                + $"code={premature.ErrorCode}, "
                + $"message={premature.SafeErrorMessage}");
        }

        Assert.Same(provider.Started.Task, startedOrCompleted);

        var stop = built.StopAsync().AsTask();
        await provider.CancellationObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        provider.Release.TrySetResult();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RunStates.Cancelled, outcome.Run.State);
        Assert.True(store.RunCancellationCommitted);
        Assert.False(store.DisposedBeforeRunCancellation);
        Assert.True(store.WasDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => built.Runtime.RunAsync(
                    new DurableRunRequest
                    {
                        Run = new AgentRun
                        {
                            RunId = "late-run",
                            AgentId = "agent-1",
                            WorldId = "world-1",
                            State = RunStates.Queued,
                            CreatedAt = now,
                            UpdatedAt = now
                        }
                    })
                .AsTask());
    }

    [Fact]
    public async Task ShutdownWaitsForBoundedDetachedToolDrainBeforeDisposal()
    {
        var directory = TempDirectory();
        var store = new DetachedDrainTrackingStore(
            Path.Combine(directory, "runtime.journal"));
        var host = new BlockingToolHost();
        var built = new GameAgentRuntimeBuilder(host)
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(new SingleToolCallProvider())
            .WithTools(new[] { SlowTool() })
            .WithSchedulerLimits(
                new ToolSchedulerLimits(
                    detachedShutdownDrainTimeoutMs: 2_000))
            .Build();
        try
        {
            var run = await built.Runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = NewRun("detached-drain-run")
                });
            Assert.Equal(RunStates.Reconciling, run.Run.State);
            Assert.Equal(1, built.Runtime.DetachedToolExecutionCount);
            Assert.Single(
                built.Runtime.GetDetachedToolExecutionSnapshot());

            var stop = built.StopAsync().AsTask();

            Assert.False(stop.IsCompleted);
            Assert.False(store.WasDisposed);
            Assert.Null(
                built.Runtime.DetachedToolExecutionsDrainedOnStop);
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => built.Runtime.RunAsync(
                        new DurableRunRequest
                        {
                            Run = NewRun("blocked-after-stop")
                        })
                    .AsTask());

            host.Release.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(
                built.Runtime.DetachedToolExecutionsDrainedOnStop);
            Assert.Equal(0, built.Runtime.DetachedToolExecutionCount);
            Assert.True(store.WasDisposed);
        }
        finally
        {
            host.Release.TrySetResult();
            await built.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ShutdownTimeoutDoesNotWaitForeverForDetachedToolExecution()
    {
        var directory = TempDirectory();
        var store = new DetachedDrainTrackingStore(
            Path.Combine(directory, "runtime.journal"));
        var host = new BlockingToolHost();
        var built = new GameAgentRuntimeBuilder(host)
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(new SingleToolCallProvider())
            .WithTools(new[] { SlowTool() })
            .WithSchedulerLimits(
                new ToolSchedulerLimits(
                    detachedShutdownDrainTimeoutMs: 25))
            .Build();
        try
        {
            var run = await built.Runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = NewRun("detached-timeout-run")
                });
            Assert.Equal(RunStates.Reconciling, run.Run.State);
            Assert.Equal(1, built.Runtime.DetachedToolExecutionCount);

            await built.StopAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(
                built.Runtime.DetachedToolExecutionsDrainedOnStop);
            Assert.Equal(1, built.Runtime.DetachedToolExecutionCount);
            Assert.True(store.WasDisposed);
        }
        finally
        {
            host.Release.TrySetResult();
            await WaitUntilAsync(
                () => built.Runtime.DetachedToolExecutionCount == 0,
                TimeSpan.FromSeconds(2));
            await built.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ThrowingCancellationCallbackCannotSkipRunDrain()
    {
        var store = new ShutdownTrackingStore();
        var provider = new ThrowingCancellationProvider();
        var built = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store, disposeOnShutdown: true)
            .AddProvider(provider)
            .Build();
        var now = DateTimeOffset.UtcNow;
        var run = built.Runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = new AgentRun
                    {
                        RunId = "throwing-callback-run",
                        AgentId = "agent-1",
                        WorldId = "world-1",
                        State = RunStates.Queued,
                        CreatedAt = now,
                        UpdatedAt = now
                    }
                })
            .AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stop = built.StopAsync().AsTask();
        await provider.CallbackInvoked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(2));
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(RunStates.Cancelled, outcome.Run.State);
        Assert.True(store.WasDisposed);
        Assert.False(provider.CleanupCompleted.Task.IsCompleted);

        provider.Release.TrySetResult();
        await provider.CleanupCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ShutdownDiscardsBufferedNotificationsWithoutOwningObserver()
    {
        var store = new ShutdownTrackingStore();
        var publisher = new BlockingDisposablePublisher();
        var built = new GameAgentRuntimeBuilder(new RejectingHost())
            .UseDurableStore(store, store)
            .PublishEventsTo(publisher)
            .AddProvider(new FinalProvider())
            .Build();
        var now = DateTimeOffset.UtcNow;

        var outcome = await built.Runtime.RunAsync(
            new DurableRunRequest
            {
                Run = new AgentRun
                {
                    RunId = "buffered-publisher-run",
                    AgentId = "agent-1",
                    WorldId = "world-1",
                    State = RunStates.Queued,
                    CreatedAt = now,
                    UpdatedAt = now
                }
            });
        Assert.Equal(RunStates.Completed, outcome.Run.State);
        await publisher.FirstPublishEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        await built.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, publisher.DisposeCount);

        publisher.ReleaseFirstPublish();
        await publisher.FirstPublishReturned.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.Equal(1, publisher.PublishCount);
        Assert.Equal(0, publisher.DisposeCount);
        publisher.Dispose();
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "game-agent-builder-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static AgentRun NewRun(string runId)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentRun
        {
            RunId = runId,
            AgentId = "agent-1",
            WorldId = "world-1",
            State = RunStates.Queued,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ToolDescriptor SlowTool()
    {
        return new ToolDescriptor
        {
            Name = "slow_tool",
            Version = "1",
            Description = "A controlled shutdown test tool.",
            ParametersSchema = ProtocolJson.ParseElement(
                """
                {
                  "type":"object",
                  "additionalProperties":false
                }
                """),
            Effect = ToolEffects.WorldCommand,
            ConflictScopes = new List<string> { "world" },
            IdempotencyPolicy = ToolIdempotencyPolicies.BestEffort,
            TimeoutMs = 10
        };
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "The expected shutdown state was not observed.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    private sealed class RejectingHost : IGameHost
    {
        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("No tool call expected.");
        }
    }

    private sealed class DenyingSkillPolicy : ISkillAdmissionPolicy
    {
        private int _activationCalls;

        public string PolicyId => "builder-skill-policy";

        public string Version => "1.0.0";

        public int ActivationCalls => Volatile.Read(ref _activationCalls);

        public SkillAdmissionDecision Evaluate(SkillAdmissionRequest request)
        {
            if (request.IsExplicitActivation)
            {
                Interlocked.Increment(ref _activationCalls);
            }

            return SkillAdmissionDecision.Deny("game_skill_denied");
        }
    }

    private sealed class FinalProvider : IStreamingModelProvider
    {
        public string ProviderId => "test";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.TextDelta,
                TextDelta = "\"ok\""
            };
            await Task.Yield();
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
                FinishReason = "stop"
            };
        }
    }

    private sealed class SingleToolCallProvider : IStreamingModelProvider
    {
        public string ProviderId => "single-tool-call";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.ToolCallDelta,
                ToolCallId = "slow-call",
                ToolNameDelta = "slow_tool",
                ArgumentsJsonDelta = "{}"
            };
            await Task.Yield();
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
        }
    }

    private sealed class BlockingToolHost : IGameHost
    {
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            await Release.Task.ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            return new ActionReceipt
            {
                OperationId = request.OperationId,
                Revision = 1,
                Status = ReceiptStatuses.Succeeded,
                Result = ProtocolJson.ParseElement("""{"released":true}"""),
                ReceivedAt = now,
                CommittedAt = now
            };
        }
    }

    private sealed class CancellableProvider : IStreamingModelProvider
    {
        public string ProviderId => "cancellable";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true
        };

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                await Release.Task;
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }

    private sealed class ThrowingCancellationProvider :
        IStreamingModelProvider
    {
        public string ProviderId => "throwing-cancellation";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true
        };

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CallbackInvoked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CleanupCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            try
            {
                using var registration = cancellationToken.Register(
                    () =>
                    {
                        CallbackInvoked.TrySetResult();
                        throw new InvalidOperationException(
                            "cancellation callback failed");
                    });
                Started.TrySetResult();
                try
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    await Release.Task;
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                CleanupCompleted.TrySetResult();
            }

            yield break;
        }
    }

    private sealed class BlockingDisposablePublisher :
        IRuntimeEventPublisher,
        IDisposable
    {
        private readonly TaskCompletionSource _releaseFirstPublish =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _publishCount;
        private int _disposeCount;

        public TaskCompletionSource FirstPublishEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstPublishReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PublishCount => Volatile.Read(ref _publishCount);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Publish(RuntimeEvent runtimeEvent)
        {
            _ = runtimeEvent;
            if (Interlocked.Increment(ref _publishCount) != 1)
            {
                return;
            }

            FirstPublishEntered.TrySetResult();
            _releaseFirstPublish.Task.GetAwaiter().GetResult();
            FirstPublishReturned.TrySetResult();
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
        }

        public void ReleaseFirstPublish()
        {
            _releaseFirstPublish.TrySetResult();
        }
    }

    private sealed class DetachedDrainTrackingStore :
        IDurableSessionStore,
        IOperationLedger
    {
        private readonly FileSessionStore _inner;

        public DetachedDrainTrackingStore(string path)
        {
            _inner = new FileSessionStore(path);
        }

        public bool WasDisposed { get; private set; }

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            return _inner.AppendAsync(runtimeEvent, cancellationToken);
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            return _inner.ReadRunAsync(runId, cancellationToken);
        }

        public ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            return _inner.AppendAtomicAsync(
                runtimeEvent,
                expectedRunRevision,
                cancellationToken);
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            return _inner.AppendAtomicBatchAsync(
                runtimeEvents,
                expectedRunRevision,
                cancellationToken);
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

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            WasDisposed = true;
        }
    }

    private sealed class ShutdownTrackingStore :
        IDurableSessionStore,
        IOperationLedger
    {
        private readonly Exception? _flushException;
        private readonly bool _blockFlush;
        private readonly bool _blockDispose;
        private readonly TaskCompletionSource _flushReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposeReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _sequence = -1;

        public ShutdownTrackingStore(
            Exception? flushException = null,
            bool blockFlush = false,
            bool blockDispose = false)
        {
            _flushException = flushException;
            _blockFlush = blockFlush;
            _blockDispose = blockDispose;
        }

        public TaskCompletionSource DisposeEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasDisposed { get; private set; }

        public bool RunCancellationCommitted { get; private set; }

        public bool DisposedBeforeRunCancellation { get; private set; }

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken) =>
            default;

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken) =>
            new(Array.Empty<RuntimeEvent>());

        public ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TrackRunCancellation(runtimeEvent);
            return new ValueTask<JournalAppendResult>(
                new JournalAppendResult(
                    Interlocked.Increment(ref _sequence),
                    checked(expectedRunRevision.GetValueOrDefault() + 1),
                    false));
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var runtimeEvent in runtimeEvents)
            {
                TrackRunCancellation(runtimeEvent);
            }

            return new ValueTask<IReadOnlyList<JournalAppendResult>>(
                runtimeEvents
                    .Select(
                        (_, index) => new JournalAppendResult(
                            Interlocked.Increment(ref _sequence),
                            checked(
                                expectedRunRevision.GetValueOrDefault()
                                + index
                                + 1),
                            false))
                    .ToArray());
        }

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default) =>
            new(new RunJournalCursor(runId, 0, 0));

        public async ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            if (_blockFlush)
            {
                await _flushReleased.Task.WaitAsync(cancellationToken);
            }

            if (_flushException is not null)
            {
                throw _flushException;
            }
        }

        public ValueTask<OperationLedgerEntry?> GetOperationAsync(
            string operationId,
            CancellationToken cancellationToken = default) =>
            new((OperationLedgerEntry?)null);

        public ValueTask<IReadOnlyList<OperationLedgerEntry>>
            ReadPendingOperationsAsync(
                string? runId = null,
                CancellationToken cancellationToken = default) =>
            new(Array.Empty<OperationLedgerEntry>());

        public ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
            RuntimeEvent receiptEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async ValueTask DisposeAsync()
        {
            DisposeEntered.TrySetResult();
            DisposedBeforeRunCancellation = !RunCancellationCommitted;
            if (_blockDispose)
            {
                await _disposeReleased.Task.ConfigureAwait(false);
            }

            WasDisposed = true;
        }

        public void ReleaseFlush()
        {
            _flushReleased.TrySetResult();
        }

        public void ReleaseDispose()
        {
            _disposeReleased.TrySetResult();
        }

        private void TrackRunCancellation(RuntimeEvent runtimeEvent)
        {
            if (runtimeEvent.Kind == RuntimeEventKinds.RunCancelled)
            {
                RunCancellationCommitted = true;
            }
        }
    }
}
