using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;
using GameAgent.Testing;

var results = new List<Measurement>
{
    await MeasureContextAsync(),
    await MeasureMemoryAsync(),
    await MeasureMultiActorAsync(1, warm: false),
    await MeasureMultiActorAsync(1, warm: true),
    await MeasureMultiActorAsync(10, warm: false),
    await MeasureMultiActorAsync(10, warm: true),
    await MeasureMultiActorAsync(100, warm: false),
    await MeasureMultiActorAsync(100, warm: true),
    await MeasureColdJournalFlushAsync(),
    await MeasureWarmJournalFlushAsync(),
    MeasureStreaming(),
    MeasureTrace()
};

foreach (var result in results)
{
    if (result.ElapsedMilliseconds > result.BudgetMilliseconds)
    {
        throw new InvalidOperationException(
            string.Format(
                CultureInfo.InvariantCulture,
                "Performance smoke '{0}' exceeded {1} ms: {2} ms.",
                result.Name,
                result.BudgetMilliseconds,
                result.ElapsedMilliseconds));
    }

    if (result.AllocatedBytes > result.BudgetAllocatedBytes)
    {
        throw new InvalidOperationException(
            string.Format(
                CultureInfo.InvariantCulture,
                "Performance smoke '{0}' exceeded {1} allocated bytes: {2}.",
                result.Name,
                result.BudgetAllocatedBytes,
                result.AllocatedBytes));
    }
}

Console.WriteLine(
    JsonSerializer.Serialize(
        new
        {
            schema = "game-agent.performance-smoke.v1",
            runtime = Environment.Version.ToString(),
            processorCount = Environment.ProcessorCount,
            results = results.Select(
                result => new
                {
                    result.Name,
                    result.Operations,
                    result.ElapsedMilliseconds,
                    result.BudgetMilliseconds,
                    result.BudgetAllocatedBytes,
                    operationsPerSecond = result.ElapsedMilliseconds == 0
                        ? result.Operations * 1_000
                        : Math.Round(
                            result.Operations * 1_000d
                            / result.ElapsedMilliseconds,
                            2),
                    result.AllocatedBytes
                })
        }));

static async Task<Measurement> MeasureContextAsync()
{
    var messages = Enumerable.Range(0, 512)
        .Select(
            index => new NormalizedMessage
            {
                MessageId = "message-" + index,
                Role = index % 2 == 0
                    ? NormalizedRoles.User
                    : NormalizedRoles.Assistant,
                CreatedAt = DateTimeOffset.UnixEpoch,
                Parts = new List<NormalizedContentPart>
                {
                    NormalizedContentPart.FromText(
                        "structured context " + index + " "
                        + new string('x', 96))
                }
            })
        .ToArray();
    var manager = new ConversationContextManager(
        new ConversationContextOptions
        {
            MaxRequestMessages = 128,
            MaxRequestUtf8Bytes = 262_144,
            RecentMessagesToKeep = 32,
            MaxSummaryUtf8Bytes = 16_384
        },
        new ExtractiveConversationCompactor(),
        new FakeRuntimeClock());

    await manager.PrepareAsync("warmup", "turn", messages);
    return await MeasureAsync(
        "context.prepare.512",
        operations: 5,
        budgetMilliseconds: 8_000,
        budgetAllocatedBytes: 768L * 1_048_576,
        async index =>
        {
            var view = await manager.PrepareAsync(
                "run-" + index,
                "turn",
                messages);
            if (view.Messages.Count > 128)
            {
                throw new InvalidOperationException(
                    "Context performance scenario exceeded its output bound.");
            }
        });
}

static async Task<Measurement> MeasureMemoryAsync()
{
    var store = new DeterministicMemoryStore(capacity: 10_000);
    for (var index = 0; index < 5_000; index++)
    {
        await store.UpsertAsync(
            new MemoryRecord(
                "memory-" + index,
                "world",
                ProtocolJson.ParseElement(
                    $$"""{"entity":"npc-{{index}}","region":"north","fact":"harbor trade route {{index}}"}"""),
                index % 5 == 0
                    ? new[] { "harbor" }
                    : Array.Empty<string>(),
                index % 100,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch),
            CancellationToken.None);
    }

    var query = new MemoryQuery(
        "world",
        ProtocolJson.ParseElement(
            """{"region":"north","fact":"harbor trade route"}"""),
        maxResults: 16);
    _ = await store.SearchAsync(query, CancellationToken.None);
    return await MeasureAsync(
        "memory.search.5000",
        operations: 100,
        budgetMilliseconds: 8_000,
        budgetAllocatedBytes: 512L * 1_048_576,
        async _ =>
        {
            var found = await store.SearchAsync(
                query,
                CancellationToken.None);
            if (found.Count > 16)
            {
                throw new InvalidOperationException(
                    "Memory performance scenario exceeded its result bound.");
            }
        });
}

static async Task<Measurement> MeasureMultiActorAsync(
    int actorCount,
    bool warm)
{
    var runtime = new PerformanceActorRuntime();
    var coordinator = new MultiActorDecisionCoordinator(
        runtime,
        new MultiActorCoordinatorOptions(
            maxBatchSize: actorCount,
            maxConcurrentRuns: Math.Min(16, actorCount)));
    if (warm)
    {
        await CoordinateAsync(-1);
    }

    var operations = warm
        ? actorCount switch
        {
            1 => 50,
            10 => 20,
            _ => 5
        }
        : 1;
    var budgetMilliseconds = warm
        ? actorCount switch
        {
            1 => 1_000,
            10 => 2_000,
            _ => 4_000
        }
        : actorCount switch
        {
            1 => 1_000,
            10 => 1_500,
            _ => 3_000
        };
    var budgetAllocatedBytes = actorCount switch
    {
        1 => 64L * 1_048_576,
        10 => 128L * 1_048_576,
        _ => 256L * 1_048_576
    };

    return await MeasureAsync(
        $"multi-actor.coordinate.{actorCount}."
        + (warm ? "warm" : "cold"),
        operations,
        budgetMilliseconds,
        budgetAllocatedBytes,
        CoordinateAsync);

    async Task CoordinateAsync(int operation)
    {
        var coordinate = new GameContextCoordinate(
            "world",
            "main",
            saveRevision: Math.Max(0, operation),
            stateVersion: "state-" + operation,
            gameTime: new GameTimePoint(
                "simulation",
                "main",
                epoch: 1,
                tick: Math.Max(0, operation)));
        var requests = Enumerable.Range(0, actorCount)
            .Select(
                actor => new DurableRunRequest
                {
                    Run = new AgentRun
                    {
                        RunId = $"perf-{actorCount}-{warm}-{operation}-{actor}",
                        AgentId = "npc-" + actor,
                        WorldId = "world",
                        DecisionKey =
                            $"decision-{actorCount}-{warm}-{operation}-{actor}",
                        State = RunStates.Queued
                    },
                    WorkloadClass = ProviderWorkloadClasses.Background
                })
            .ToArray();
        var outcome = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                $"perf-batch-{actorCount}-{warm}-{operation}",
                coordinate,
                requests,
                new MultiActorBatchBudget(
                    maxTokens: actorCount * 8_000L,
                    maxActions: actorCount * 8L,
                    maxDurationMs: actorCount * 30_000L,
                    maxCostUsd: actorCount.ToString(
                        CultureInfo.InvariantCulture))));
        if (outcome.Results.Count != actorCount
            || outcome.Results.Any(result => !result.Succeeded)
            || outcome.Manifest.BudgetReservation?.ReservedTokens
                != actorCount * 8_000L)
        {
            throw new InvalidOperationException(
                "Multi-actor performance scenario lost an actor or budget reservation.");
        }
    }
}

static async Task<Measurement> MeasureColdJournalFlushAsync()
{
    return await MeasureAsync(
        "journal.file.flush.cold",
        operations: 1,
        budgetMilliseconds: 2_000,
        budgetAllocatedBytes: 32L * 1_048_576,
        async operation =>
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "game-agent-performance-"
                + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "runtime.journal");
            try
            {
                await using var store = new FileSessionStore(
                    path,
                    new FileJournalOptions
                    {
                        FlushToDiskOnAppend = true
                    });
                await store.AppendAsync(
                    JournalEvent("cold", operation),
                    CancellationToken.None);
                await store.FlushAsync(CancellationToken.None);
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        });
}

static async Task<Measurement> MeasureWarmJournalFlushAsync()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        "game-agent-performance-" + Guid.NewGuid().ToString("N"));
    var path = Path.Combine(directory, "runtime.journal");
    try
    {
        await using var store = new FileSessionStore(
            path,
            new FileJournalOptions
            {
                FlushToDiskOnAppend = true
            });
        await store.AppendAsync(
            JournalEvent("warm", 0),
            CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);
        return await MeasureAsync(
            "journal.file.flush.warm",
            operations: 20,
            budgetMilliseconds: 4_000,
            budgetAllocatedBytes: 32L * 1_048_576,
            async operation =>
            {
                await store.AppendAsync(
                    JournalEvent("warm", operation + 1),
                    CancellationToken.None);
                await store.FlushAsync(CancellationToken.None);
            });
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static RuntimeEvent JournalEvent(string scenario, int sequence)
{
    return new RuntimeEvent
    {
        EventId = $"perf-journal-{scenario}-{sequence}",
        RunId = "perf-journal-" + scenario,
        Sequence = sequence,
        Kind = RuntimeEventKinds.AssistantDelta,
        Durability = EventDurabilities.Durable,
        RuntimeGeneration = 1,
        Timestamp = DateTimeOffset.UnixEpoch.AddTicks(sequence),
        Payload = ProtocolJson.ParseElement(
            $$"""{"sequence":{{sequence}},"text":"journal flush"}""")
    };
}

static Measurement MeasureStreaming()
{
    var regular = Measure(
        "stream.coalesce.10000",
        operations: 10_000,
        budgetMilliseconds: 4_000,
        budgetAllocatedBytes: 512L * 1_048_576,
        action: index =>
        {
            var coalescer = new StreamingTextCoalescer(
                new StreamingPresentationOptions
                {
                    TargetChunkUtf8Bytes = 256,
                    MaximumBufferedUtf8Bytes = 1_024,
                    IdleFlushInterval = TimeSpan.FromMilliseconds(100)
                });
            var complete = new System.Text.StringBuilder();
            for (var delta = 0; delta < 32; delta++)
            {
                var value = "delta-" + index + "-" + delta;
                complete.Append(value);
                _ = coalescer.Push(value, DateTimeOffset.UnixEpoch);
            }
            _ = coalescer.Complete(complete.ToString());
        });
    var oversized = new string('x', 4 * 1024 * 1024);
    var largeDelta = Measure(
        "stream.reject-large-delta.4mib",
        operations: 1,
        budgetMilliseconds: 4_000,
        budgetAllocatedBytes: 4L * 1_048_576,
        action: unused =>
        {
            _ = unused;
            var coalescer = new StreamingTextCoalescer(
                new StreamingPresentationOptions
                {
                    TargetChunkUtf8Bytes = 1_024,
                    MaximumBufferedUtf8Bytes = 4_096,
                    IdleFlushInterval = TimeSpan.FromMilliseconds(100)
                });
            try
            {
                _ = coalescer.Push(
                    oversized,
                    DateTimeOffset.UnixEpoch);
                throw new InvalidOperationException(
                    "Large streaming delta did not fail its input bound.");
            }
            catch (RuntimeContentLimitException exception)
                when (exception.LimitCode
                      == "stream_delta_bytes_exceeded")
            {
            }
        });
    return new Measurement(
        regular.Name + "+" + largeDelta.Name,
        regular.Operations + largeDelta.Operations,
        regular.ElapsedMilliseconds + largeDelta.ElapsedMilliseconds,
        regular.BudgetMilliseconds + largeDelta.BudgetMilliseconds,
        regular.BudgetAllocatedBytes + largeDelta.BudgetAllocatedBytes,
        regular.AllocatedBytes + largeDelta.AllocatedBytes);
}

static Measurement MeasureTrace()
{
    var events = Enumerable.Range(0, 1_000)
        .Select(
            index => new RuntimeEvent
            {
                EventId = "event-" + index,
                RunId = "run",
                Sequence = index,
                Kind = index == 999
                    ? RuntimeEventKinds.RunCompleted
                    : RuntimeEventKinds.AssistantDelta,
                Durability = EventDurabilities.Durable,
                RuntimeGeneration = 1,
                Timestamp = DateTimeOffset.UnixEpoch,
                Payload = ProtocolJson.ParseElement(
                    $$"""{"index":{{index}},"text":"safe"}""")
            })
        .ToArray();
    var exporter = new RuntimeTraceExporter();
    return Measure(
        "trace.export.1000",
        operations: 20,
        budgetMilliseconds: 4_000,
        budgetAllocatedBytes: 512L * 1_048_576,
        action: _ =>
        {
            var export = exporter.Export(events);
            if (export.EventCount != 1_000)
            {
                throw new InvalidOperationException(
                    "Trace performance scenario lost events.");
            }
        });
}

static async Task<Measurement> MeasureAsync(
    string name,
    int operations,
    long budgetMilliseconds,
    long budgetAllocatedBytes,
    Func<int, Task> action)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var before = GC.GetTotalAllocatedBytes(precise: true);
    var watch = Stopwatch.StartNew();
    for (var index = 0; index < operations; index++)
    {
        await action(index);
    }
    watch.Stop();
    return new Measurement(
        name,
        operations,
        watch.ElapsedMilliseconds,
        budgetMilliseconds,
        budgetAllocatedBytes,
        Math.Max(
            0,
            GC.GetTotalAllocatedBytes(precise: true) - before));
}

static Measurement Measure(
    string name,
    int operations,
    long budgetMilliseconds,
    long budgetAllocatedBytes,
    Action<int> action)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var before = GC.GetAllocatedBytesForCurrentThread();
    var watch = Stopwatch.StartNew();
    for (var index = 0; index < operations; index++)
    {
        action(index);
    }
    watch.Stop();
    return new Measurement(
        name,
        operations,
        watch.ElapsedMilliseconds,
        budgetMilliseconds,
        budgetAllocatedBytes,
        Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - before));
}

internal sealed class Measurement
{
    public Measurement(
        string name,
        int operations,
        long elapsedMilliseconds,
        long budgetMilliseconds,
        long budgetAllocatedBytes,
        long allocatedBytes)
    {
        Name = name;
        Operations = operations;
        ElapsedMilliseconds = elapsedMilliseconds;
        BudgetMilliseconds = budgetMilliseconds;
        BudgetAllocatedBytes = budgetAllocatedBytes;
        AllocatedBytes = allocatedBytes;
    }

    public string Name { get; }

    public int Operations { get; }

    public long ElapsedMilliseconds { get; }

    public long BudgetMilliseconds { get; }

    public long BudgetAllocatedBytes { get; }

    public long AllocatedBytes { get; }
}

internal sealed class PerformanceActorRuntime : IDurableAgentRuntime
{
    public RuntimeControlPlane Controls { get; } = new();

    public async ValueTask<DurableRunOutcome> RunAsync(
        DurableRunRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        request.Run.State = RunStates.Completed;
        return new DurableRunOutcome
        {
            Run = request.Run,
            FinalOutput = ProtocolJson.ParseElement("""{"intent":"idle"}""")
        };
    }

    public ValueTask<DurableRunOutcome> ResumeAsync(
        string runId,
        DurableRunContinuation? continuation = null,
        IGameOperationReconciler? reconciler = null,
        CancellationToken cancellationToken = default)
    {
        _ = runId;
        _ = continuation;
        _ = reconciler;
        _ = cancellationToken;
        throw new NotSupportedException();
    }
}
