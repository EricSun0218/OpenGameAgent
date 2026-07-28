using System.Text.Json;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;
using GameAgent.Providers.OpenAICompatible;

var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
if (string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("DEEPSEEK_LIVE_SKIPPED credential_missing");
    return 2;
}

var baseUrl = Environment.GetEnvironmentVariable("DEEPSEEK_BASE_URL")
              ?? "https://api.deepseek.com";
var directory = Path.Combine(
    Path.GetTempPath(),
    "game-agent-live-smoke",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);

try
{
    await using var store = new FileSessionStore(
        Path.Combine(directory, "live.journal"));
    using var transport = new HttpClientStreamingTransport();
    var clock = new LiveClock();
    var ids = new LiveIds();
    var provider = new OpenAiCompatibleStreamingProvider(
        new OpenAiCompatibleProviderOptions
        {
            ProviderId = "deepseek",
            BaseUri = new Uri(baseUrl, UriKind.Absolute),
            Model = "deepseek-v4-pro",
            ThinkingMode = "enabled",
            ReasoningEffort = "high",
            MaxOutputTokens = 4_096
        },
        new StaticBearerTokenSource(key),
        transport);
    key = string.Empty;

    var runner = new ProviderAttemptRunner(
        new[] { provider },
        new ProviderRetryPolicy
        {
            MaxAttemptsPerProvider = 2,
            InitialDelay = TimeSpan.FromMilliseconds(500),
            MaxDelay = TimeSpan.FromSeconds(5),
            IdleTimeout = TimeSpan.FromSeconds(60),
            TotalTimeout = TimeSpan.FromMinutes(3)
        },
        new SystemRuntimeDelay(),
        ids);
    var journal = new JournalCoordinator(store, store, clock, ids);
    var tools = new ToolCatalogRegistry();
    tools.Replace(
        new[]
        {
            new ToolDescriptor
            {
                Name = "inspect_state",
                Version = "1",
                Description =
                    "Read the authoritative structured state for one game entity.",
                ParametersSchema = Json(
                    """
                    {
                      "type":"object",
                      "properties":{"entityId":{"type":"string"}},
                      "required":["entityId"],
                      "additionalProperties":false
                    }
                    """),
                Effect = ToolEffects.PureRead,
                ConflictScopes = new List<string> { "entity:{entityId}" }
            }
        });
    var host = new LiveHost(clock);
    await using var runtime = new DurableAgentRuntime(
        runner,
        host,
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
            ModelId = "deepseek-v4-pro",
            MaxConcurrentProviderCalls = 1
        });
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
    var now = clock.UtcNow;
    var outcome = await runtime.RunAsync(
        new DurableRunRequest
        {
            Run = new AgentRun
            {
                RunId = "live-" + Guid.NewGuid().ToString("N"),
                AgentId = "smoke-agent",
                WorldId = "smoke-world",
                SessionId = "smoke-session",
                State = RunStates.Queued,
                Budget = new AgentBudget
                {
                    MaxTurns = 4,
                    MaxActions = 2,
                    MaxTokens = 16_000,
                    MaxDurationMs = 220_000,
                    MaxCostUsd = "0.10"
                },
                CreatedAt = now,
                UpdatedAt = now
            },
            InitialTranscript = new[]
            {
                new NormalizedMessage
                {
                    MessageId = "live-system",
                    Role = NormalizedRoles.System,
                    CreatedAt = now,
                    Parts = new List<NormalizedContentPart>
                    {
                        NormalizedContentPart.FromText(
                            "Execute this protocol check: call inspect_state exactly "
                            + "once for entityId npc-42, then return a compact JSON "
                            + "object whose status is completed. Do not echo secrets.")
                    }
                }
            },
            Context = new[]
            {
                new ContextCandidate(
                    "live-context",
                    "provider_smoke",
                    Json(
                        """
                        {
                          "request":{"kind":"provider_smoke","step":1},
                          "entity":{"id":"npc-42","stateVersion":"7"}
                        }
                        """),
                    priority: 100,
                    required: true,
                    canDefer: false)
            }
        },
        timeout.Token);

    if (!string.Equals(
            outcome.Run.State,
            RunStates.Completed,
            StringComparison.Ordinal)
        || host.CallCount != 1
        || outcome.Run.Usage.InputTokens <= 0
        || outcome.Run.Usage.OutputTokens <= 0)
    {
        Console.Error.WriteLine(
            "DEEPSEEK_LIVE_FAIL "
            + $"state={outcome.Run.State} hostCalls={host.CallCount} "
            + $"code={outcome.ErrorCode ?? "none"}");
        return 1;
    }

    Console.WriteLine(
        "DEEPSEEK_LIVE_PASS "
        + "model=deepseek-v4-pro "
        + $"turns={outcome.Run.Usage.Turns} "
        + $"actions={outcome.Run.Usage.Actions} "
        + $"tokens={outcome.Run.Usage.InputTokens + outcome.Run.Usage.OutputTokens} "
        + $"durationMs={outcome.Run.Usage.DurationMs} "
        + $"costUsd={outcome.Run.Usage.CostUsd}");
    return 0;
}
catch (ProviderException exception)
{
    Console.Error.WriteLine(
        $"DEEPSEEK_LIVE_FAIL code={exception.Code} category={exception.Category}");
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine(
        $"DEEPSEEK_LIVE_FAIL exception={exception.GetType().Name}");
    return 1;
}
finally
{
    key = string.Empty;
    if (Directory.Exists(directory))
    {
        Directory.Delete(directory, recursive: true);
    }
}

static JsonElement Json(string value)
{
    using var document = JsonDocument.Parse(value);
    return document.RootElement.Clone();
}

internal sealed class LiveClock : IRuntimeClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

internal sealed class LiveIds : IRuntimeIdGenerator
{
    public string NewId(string category)
    {
        return category + "-" + Guid.NewGuid().ToString("N");
    }
}

internal sealed class LiveHost : IGameHost
{
    private readonly IRuntimeClock _clock;

    public LiveHost(IRuntimeClock clock)
    {
        _clock = clock;
    }

    public int CallCount { get; private set; }

    public ValueTask<ActionReceipt> SubmitActionAsync(
        ActionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        return new ValueTask<ActionReceipt>(
            new ActionReceipt
            {
                OperationId = request.OperationId,
                Revision = 1,
                Status = ReceiptStatuses.Succeeded,
                Result = JsonDocument.Parse(
                    """
                    {
                      "entityId":"npc-42",
                      "hp":73,
                      "position":{"x":12,"y":4},
                      "stateVersion":"8"
                    }
                    """).RootElement.Clone(),
                ReceivedAt = _clock.UtcNow,
                CommittedAt = _clock.UtcNow
            });
    }
}
