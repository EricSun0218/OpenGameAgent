using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;
using GameAgent.Runtime;

namespace GameAgent.Godot.Samples;

public sealed class SampleRuntimeFixture
{
    public DurableRunRequest Request { get; init; } = new();

    public IReadOnlyList<ObservationEnvelope> Observations { get; init; } =
        Array.Empty<ObservationEnvelope>();

    public SampleDurableStore Store { get; init; } = null!;

    public SampleMainThreadProbe MainThreadProbe { get; init; } = null!;

    public SampleStreamingProvider Provider { get; init; } = null!;
}

public sealed class SampleMainThreadProbe
{
    public bool HandlerRan { get; set; }

    public bool HandlerRanOnMainThread { get; set; }

    public bool ProviderRanOffMainThread { get; set; }
}

public static class SampleRuntimeFactory
{
    public static SampleRuntimeFixture Configure(GameAgentRuntimeNode node)
    {
        var clock = new SystemRuntimeClock();
        var ids = new SampleIdGenerator();
        var store = SampleDurableStore.Create();
        var probe = new SampleMainThreadProbe();
        var provider = new SampleStreamingProvider(probe);
        var host = new GodotMainThreadGameHost(node.Dispatcher, clock);
        host.Register(
            "gather_food",
            request =>
            {
                probe.HandlerRan = true;
                probe.HandlerRanOnMainThread =
                    global::Godot.OS.GetThreadCallerId()
                    == global::Godot.OS.GetMainThreadId();
                return new ActionReceipt
                {
                    OperationId = request.OperationId,
                    Revision = 1,
                    Status = ReceiptStatuses.Succeeded,
                    Result = ProtocolJson.ParseElement(
                        """{"resource":"berries","gathered":1}"""),
                    Retryable = false,
                    CommittedAt = clock.UtcNow,
                    ReceivedAt = clock.UtcNow
                };
            });

        var tool = CreateTool();
        var built = new GameAgentRuntimeBuilder(host)
            .UseDurableStore(
                store,
                store,
                disposeOnShutdown: true)
            .AddProvider(provider)
            .WithTools(new[] { tool })
            .WithRuntimeServices(clock, ids)
            .WithRetryPolicy(
                new ProviderRetryPolicy
                {
                    MaxAttemptsPerProvider = 1,
                    IdleTimeout = TimeSpan.FromSeconds(5),
                    TotalTimeout = TimeSpan.FromSeconds(15)
                })
            .WithRuntimeOptions(
                new DurableAgentRuntimeOptions
                {
                    ModelId = "godot-sample-model",
                    MaxConcurrentProviderCalls = 4
                })
            .PublishEventsTo(node.Typed.EventPublisher)
            .Build();
        node.Typed.ConfigureDurable(built);

        var now = clock.UtcNow;
        var observation = CreateObservation(
            "godot-sample-observation",
            "sample-world",
            """{"hunger":70,"visibleResources":["berries"]}""",
            now);
        return new SampleRuntimeFixture
        {
            Store = store,
            MainThreadProbe = probe,
            Provider = provider,
            Observations = new[] { observation },
            Request = new DurableRunRequest
            {
                Run = CreateRun(
                    "godot-sample-run",
                    "sample-world",
                    now),
                Context = new[]
                {
                    ContextCandidate.FromObservation(
                        observation,
                        required: true,
                        canDefer: false)
                }
            }
        };
    }

    public static AgentRun CreateRun(
        string runId,
        string worldId,
        DateTimeOffset now)
    {
        return new AgentRun
        {
            RunId = runId,
            AgentId = "forager",
            WorldId = worldId,
            SessionId = "godot-sample-session",
            Trigger = new AgentTrigger { Type = "manual" },
            State = RunStates.Queued,
            RuntimeGeneration = 1,
            Budget = new AgentBudget
            {
                MaxTurns = 6,
                MaxDurationMs = 30_000,
                MaxTokens = 8_000,
                MaxCostUsd = "1",
                MaxActions = 4
            },
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static ObservationEnvelope CreateObservation(
        string observationId,
        string worldId,
        string payload,
        DateTimeOffset now)
    {
        return new ObservationEnvelope
        {
            ObservationId = observationId,
            WorldId = worldId,
            Source = "sample.world",
            Kind = "snapshot",
            ContentType = "application/json",
            ContentSchemaVersion = "1",
            Payload = ProtocolJson.ParseElement(payload),
            ObservedAt = now,
            Trust = "authoritative",
            Visibility = new VisibilityRule
            {
                Scope = "agent",
                AudienceIds = new List<string> { "forager" }
            },
            Priority = 100
        };
    }

    private static ToolDescriptor CreateTool()
    {
        return new ToolDescriptor
        {
            Name = "gather_food",
            Version = "1",
            Description = "Gather one visible food resource.",
            ParametersSchema = ProtocolJson.ParseElement(
                """
                {
                  "type":"object",
                  "properties":{"resource":{"type":"string"}},
                  "required":["resource"],
                  "additionalProperties":false
                }
                """),
            Effect = ToolEffects.WorldCommand,
            ConflictScopes = new List<string> { "resource:{resource}" },
            ThreadAffinity = ThreadAffinities.EngineMainThread,
            TimeoutMs = 2_000,
            RetryPolicy = "idempotent",
            IdempotencyPolicy = "required",
            Toolset = "sample"
        };
    }

    private sealed class SampleIdGenerator : IRuntimeIdGenerator
    {
        private long _value;

        public string NewId(string category) =>
            $"{category}-{Interlocked.Increment(ref _value):D8}";
    }
}

public sealed class SampleStreamingProvider : IStreamingModelProvider
{
    private readonly ConcurrentDictionary<string, int> _attempts =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _started =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _release =
        new(StringComparer.Ordinal);
    private readonly object _requestGate = new();
    private readonly List<StreamingModelRequest> _requests = new();
    private readonly SampleMainThreadProbe _probe;

    public SampleStreamingProvider(SampleMainThreadProbe probe)
    {
        _probe = probe;
    }

    public string ProviderId => "godot-sample-provider";

    public ProviderCapabilities Capabilities { get; } = new()
    {
        Streaming = true,
        ToolCalling = true,
        JsonOutput = true,
        MaxContextTokens = 100_000
    };

    public async Task WaitForAttemptAsync(
        string runId,
        int attempt,
        TimeSpan timeout)
    {
        await StartedSignal(runId, attempt).Task.WaitAsync(timeout);
    }

    public void Release(string runId, int attempt)
    {
        ReleaseSignal(runId, attempt).TrySetResult();
    }

    public IReadOnlyList<StreamingModelRequest> RequestsFor(string runId)
    {
        lock (_requestGate)
        {
            return _requests
                .Where(item => string.Equals(
                    item.RunId,
                    runId,
                    StringComparison.Ordinal))
                .ToArray();
        }
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        StreamingModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _probe.ProviderRanOffMainThread =
            global::Godot.OS.GetThreadCallerId()
            != global::Godot.OS.GetMainThreadId();
        lock (_requestGate)
        {
            _requests.Add(request);
        }

        var attempt = _attempts.AddOrUpdate(
            request.RunId,
            1,
            static (_, current) => checked(current + 1));
        var waitForSteerUsage =
            request.RunId.StartsWith(
                "control-steer",
                StringComparison.Ordinal)
            && attempt == 1;
        if (!waitForSteerUsage)
        {
            StartedSignal(request.RunId, attempt).TrySetResult();
        }

        if (string.Equals(
                request.RunId,
                "godot-sample-run",
                StringComparison.Ordinal))
        {
            if (attempt == 1)
            {
                yield return ToolDelta(
                    request,
                    0,
                    "godot-sample-tool-call",
                    "gather_food",
                    """{"resource":"berries"}""");
                yield return Usage(request, 1);
                yield return Completed(request, 2, "tool_calls");
                yield break;
            }

            yield return Text(
                request,
                0,
                """{"decision":"eat","resource":"berries"}""");
            yield return Usage(request, 1);
            yield return Completed(request, 2, "stop");
            yield break;
        }

        if (request.RunId.StartsWith("control-cancel", StringComparison.Ordinal)
            || request.RunId.StartsWith(
                "control-interrupt",
                StringComparison.Ordinal))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        if (waitForSteerUsage)
        {
            yield return Usage(request, 0);
            StartedSignal(request.RunId, attempt).TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        if (request.RunId.StartsWith("control-follow-up", StringComparison.Ordinal)
            && attempt == 1)
        {
            await ReleaseSignal(request.RunId, attempt)
                .Task
                .WaitAsync(cancellationToken);
            yield return Text(request, 0, "first-answer");
            yield return Usage(request, 1);
            yield return Completed(request, 2, "stop");
            yield break;
        }

        var final = request.RunId.StartsWith(
            "control-steer",
            StringComparison.Ordinal)
            ? "steered-final"
            : request.RunId.StartsWith(
                "control-follow-up",
                StringComparison.Ordinal)
                ? "follow-up-final"
                : "completed";
        yield return Text(request, 0, final);
        yield return Usage(request, 1);
        yield return Completed(request, 2, "stop");
    }

    private TaskCompletionSource StartedSignal(string runId, int attempt) =>
        _started.GetOrAdd(
            Key(runId, attempt),
            static _ => new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously));

    private TaskCompletionSource ReleaseSignal(string runId, int attempt) =>
        _release.GetOrAdd(
            Key(runId, attempt),
            static _ => new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously));

    private static string Key(string runId, int attempt) =>
        $"{runId}:{attempt}";

    private static ModelStreamEvent ToolDelta(
        StreamingModelRequest request,
        long ordinal,
        string toolCallId,
        string toolName,
        string arguments)
    {
        return new ModelStreamEvent
        {
            StreamAttemptId = request.StreamAttemptId,
            Ordinal = ordinal,
            Kind = ModelStreamEventKinds.ToolCallDelta,
            ToolCallId = toolCallId,
            ToolNameDelta = toolName,
            ArgumentsJsonDelta = arguments
        };
    }

    private static ModelStreamEvent Text(
        StreamingModelRequest request,
        long ordinal,
        string text)
    {
        return new ModelStreamEvent
        {
            StreamAttemptId = request.StreamAttemptId,
            Ordinal = ordinal,
            Kind = ModelStreamEventKinds.TextDelta,
            TextDelta = text
        };
    }

    private static ModelStreamEvent Completed(
        StreamingModelRequest request,
        long ordinal,
        string reason)
    {
        return new ModelStreamEvent
        {
            StreamAttemptId = request.StreamAttemptId,
            Ordinal = ordinal,
            Kind = ModelStreamEventKinds.Completed,
            FinishReason = reason
        };
    }

    private static ModelStreamEvent Usage(
        StreamingModelRequest request,
        long ordinal)
    {
        return new ModelStreamEvent
        {
            StreamAttemptId = request.StreamAttemptId,
            Ordinal = ordinal,
            Kind = ModelStreamEventKinds.Usage,
            Usage = new ProviderUsage
            {
                InputTokens = 0,
                OutputTokens = 0,
                CostUsd = "0"
            }
        };
    }
}

public sealed class SampleDurableStore :
    IDurableSessionStore,
    IOperationLedger,
    IAtomicJournalBatchStore
{
    private readonly FileSessionStore _inner;
    private readonly string _directory;
    private int _disposed;
    private int _flushCount;

    private SampleDurableStore(
        FileSessionStore inner,
        string directory)
    {
        _inner = inner;
        _directory = directory;
    }

    public int FlushCount => Volatile.Read(ref _flushCount);

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public static SampleDurableStore Create()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-godot-sample",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new SampleDurableStore(
            new FileSessionStore(Path.Combine(directory, "runtime.journal")),
            directory);
    }

    public ValueTask AppendAsync(
        RuntimeEvent runtimeEvent,
        CancellationToken cancellationToken) =>
        _inner.AppendAsync(runtimeEvent, cancellationToken);

    public ValueTask<JournalAppendResult> AppendAtomicAsync(
        RuntimeEvent runtimeEvent,
        long? expectedRunRevision = null,
        CancellationToken cancellationToken = default) =>
        _inner.AppendAtomicAsync(
            runtimeEvent,
            expectedRunRevision,
            cancellationToken);

    public ValueTask<IReadOnlyList<JournalAppendResult>> AppendAtomicBatchAsync(
        IReadOnlyList<RuntimeEvent> runtimeEvents,
        long? expectedRunRevision = null,
        CancellationToken cancellationToken = default) =>
        _inner.AppendAtomicBatchAsync(
            runtimeEvents,
            expectedRunRevision,
            cancellationToken);

    public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
        string runId,
        CancellationToken cancellationToken) =>
        _inner.ReadRunAsync(runId, cancellationToken);

    public ValueTask<RunJournalCursor> GetRunCursorAsync(
        string runId,
        CancellationToken cancellationToken = default) =>
        _inner.GetRunCursorAsync(runId, cancellationToken);

    public ValueTask<OperationLedgerEntry?> GetOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default) =>
        _inner.GetOperationAsync(operationId, cancellationToken);

    public ValueTask<IReadOnlyList<OperationLedgerEntry>>
        ReadPendingOperationsAsync(
            string? runId = null,
            CancellationToken cancellationToken = default) =>
        _inner.ReadPendingOperationsAsync(runId, cancellationToken);

    public ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
        RuntimeEvent receiptEvent,
        long? expectedRunRevision = null,
        CancellationToken cancellationToken = default) =>
        _inner.ReconcileReceiptAsync(
            receiptEvent,
            expectedRunRevision,
            cancellationToken);

    public async ValueTask FlushAsync(
        CancellationToken cancellationToken = default)
    {
        await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _flushCount);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _inner.DisposeAsync().ConfigureAwait(false);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
