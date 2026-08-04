using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;
using GameAgent.Simulation;
using GameAgent.Storage.Sqlite;
using GameAgent.Testing;

var seconds = ReadDuration(args);
var deadline = Stopwatch.StartNew();
var root = Path.Combine(Path.GetTempPath(), "game-agent-soak-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var database = Path.Combine(root, "runtime.db");
var connection = "Data Source=" + database + ";Pooling=False";
var latencies = new BoundedLatencies(10_000);
var actors = Enumerable.Range(0, 10_000).Select(index => new LivingWorldActorSignal
{
    ActorId = "npc-" + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture),
    PendingTriggers = index % 17 == 0 ? 1 : 0,
    Salience = (index % 100) / 100d,
    LastEvaluatedGameTick = 0,
    EstimatedTokens = 64,
    EstimatedSteps = 1
}).ToArray();
var policy = new LivingWorldPolicy(new LivingWorldPolicyOptions
{
    MaxActorsPerCycle = 32,
    MaxForegroundActors = 8,
    MaxNearbyActors = 16,
    MaxBackgroundActors = 8,
    MaxEstimatedTokensPerCycle = 2_048,
    MaxEstimatedStepsPerCycle = 32,
    DormantAfterGameTicks = 1_000,
    StarvationAfterGameTicks = 500
});
long iteration = 0;
long revision = 0;
var segment = 0;
var restarts = 0;
var contentionConflicts = 0;
var providerFailovers = 0;
var aggregated = 0L;
SqliteSessionStore? store = null;
try
{
    store = new SqliteSessionStore(connection);
    while (deadline.Elapsed < TimeSpan.FromSeconds(seconds) || iteration == 0)
    {
        var started = Stopwatch.GetTimestamp();
        var gameTick = checked(2_000 + iteration);
        var plan = policy.Plan(new LivingWorldCycle
        {
            WorldId = "soak-world",
            GameTick = gameTick
        }, actors);
        if (plan.Runnable.Count > 32
            || plan.Runnable.Sum(item => actors[int.Parse(item.ActorId.AsSpan(4))].EstimatedTokens) > 2_048)
        {
            throw new InvalidOperationException("Living-world soak exceeded its admission budget.");
        }
        aggregated += plan.Decisions.Count(static item => item.Decision == LivingWorldDecisionKinds.Aggregate);

        var notices = new List<ProviderAttemptNotice>();
        var providerResult = await CreateFaultRunner().RunAsync(
            "soak-provider-" + iteration,
            "attempt-" + iteration,
            "turn",
            Array.Empty<NormalizedMessage>(),
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None,
            notices.Add);
        if (providerResult.ProviderId != "healthy" || providerResult.Text != "ok"
            || notices.All(static notice => notice.ErrorCode != "injected_route_failure"))
        {
            throw new InvalidOperationException("Provider fault injection did not fail over safely.");
        }
        providerFailovers++;

        var runId = "segment-" + segment;
        await store.AppendAtomicAsync(Event("event-" + iteration, runId, iteration), revision);
        revision++;
        if (iteration > 0 && iteration % 5 == 0)
        {
            await using var competing = new SqliteSessionStore(connection);
            var attempts = new[]
            {
                store.AppendAtomicAsync(Event("contend-a-" + iteration, runId, iteration), revision).AsTask(),
                competing.AppendAtomicAsync(Event("contend-b-" + iteration, runId, iteration), revision).AsTask()
            };
            try { await Task.WhenAll(attempts); } catch { }
            if (attempts.Count(static task => task.Status == TaskStatus.RanToCompletion) != 1)
            {
                throw new InvalidOperationException("SQLite contention did not elect exactly one revision writer.");
            }
            contentionConflicts++;
            revision++;
        }
        if (iteration > 0 && iteration % 10 == 0)
        {
            await store.DisposeAsync();
            store = new SqliteSessionStore(connection);
            var cursor = await store.GetRunCursorAsync(runId);
            if (cursor.Revision != revision) throw new InvalidOperationException("SQLite restart lost a committed revision.");
            restarts++;
        }
        if (iteration > 0 && iteration % 1_000 == 0)
        {
            await store.DisposeAsync();
            store = null;
            Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            segment++;
            revision = 0;
            store = new SqliteSessionStore(connection);
        }
        latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        iteration++;
    }
}
finally
{
    if (store is not null) await store.DisposeAsync();
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    schema = "game-agent.soak-smoke.v1",
    configuredSeconds = seconds,
    elapsedSeconds = Math.Round(deadline.Elapsed.TotalSeconds, 3),
    iterations = iteration,
    providerFailovers,
    sqliteRestarts = restarts,
    sqliteContentionConflicts = contentionConflicts,
    dormantSignalsAggregated = aggregated,
    latencyMilliseconds = new
    {
        p50 = latencies.Percentile(0.50),
        p95 = latencies.Percentile(0.95),
        p99 = latencies.Percentile(0.99),
        max = latencies.Maximum
    },
    workingSetBytes = Environment.WorkingSet
}));

static int ReadDuration(string[] arguments)
{
    var value = Environment.GetEnvironmentVariable("GAME_AGENT_SOAK_SECONDS");
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (arguments[index] == "--seconds") value = arguments[index + 1];
    }
    if (!int.TryParse(value ?? "5", out var seconds) || seconds is < 1 or > 86_400)
    {
        throw new ArgumentOutOfRangeException(nameof(arguments), "Soak duration must be between 1 and 86400 seconds.");
    }
    return seconds;
}

static ProviderAttemptRunner CreateFaultRunner() => new(
    new IStreamingModelProvider[] { new FailingProvider(), new HealthyProvider() },
    new ProviderRetryPolicy
    {
        MaxAttemptsPerProvider = 1,
        InitialDelay = TimeSpan.Zero,
        MaxDelay = TimeSpan.Zero,
        IdleTimeout = TimeSpan.FromSeconds(1),
        TotalTimeout = TimeSpan.FromSeconds(2)
    },
    new ImmediateDelay(),
    new SequentialIdGenerator());

static RuntimeEvent Event(string id, string runId, long sequence) => new()
{
    EventId = id,
    RunId = runId,
    Sequence = sequence,
    Kind = "soak.tick",
    Durability = EventDurabilities.Durable,
    Timestamp = DateTimeOffset.UnixEpoch.AddTicks(sequence),
    Payload = ProtocolJson.ParseElement("{\"ok\":true}")
};

sealed class FailingProvider : IStreamingModelProvider
{
    public string ProviderId => "failing";
    public ProviderCapabilities Capabilities { get; } = new();
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        StreamingModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _ = request;
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        throw new ProviderException(
            "injected_route_failure",
            "soak",
            "An injected provider route failure occurred.",
            ProviderFailureDisposition.Failover,
            usageKnownToBeZero: true);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}

sealed class HealthyProvider : IStreamingModelProvider
{
    public string ProviderId => "healthy";
    public ProviderCapabilities Capabilities { get; } = new();
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        StreamingModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return new ModelStreamEvent
        {
            StreamAttemptId = request.StreamAttemptId,
            Ordinal = 0,
            Kind = ModelStreamEventKinds.TextDelta,
            TextDelta = "ok"
        };
        yield return new ModelStreamEvent
        {
            StreamAttemptId = request.StreamAttemptId,
            Ordinal = 1,
            Kind = ModelStreamEventKinds.Usage,
            Usage = new ProviderUsage { InputTokens = 1, OutputTokens = 1, CostUsd = "0" }
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

sealed class ImmediateDelay : IRuntimeDelay
{
    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

sealed class BoundedLatencies
{
    private readonly double[] _values;
    private int _count;
    private int _next;
    public BoundedLatencies(int capacity) => _values = new double[capacity];
    public double Maximum => _values.Take(_count).DefaultIfEmpty().Max();
    public void Add(double value)
    {
        _values[_next] = value;
        _next = (_next + 1) % _values.Length;
        if (_count < _values.Length) _count++;
    }
    public double Percentile(double percentile)
    {
        var copy = _values.Take(_count).Order().ToArray();
        if (copy.Length == 0) return 0;
        var index = (int)Math.Ceiling(percentile * copy.Length) - 1;
        return Math.Round(copy[Math.Clamp(index, 0, copy.Length - 1)], 3);
    }
}
