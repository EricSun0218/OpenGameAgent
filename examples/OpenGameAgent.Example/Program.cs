using System.Text.Json;
using OpenGameAgent;
using OpenGameAgent.Kernel;
using OpenGameAgent.Providers.OpenAICompatible;

var endpointText = Environment.GetEnvironmentVariable("OGA_MODEL_ENDPOINT")
    ?? throw new InvalidOperationException("Set OGA_MODEL_ENDPOINT to a chat-completions endpoint.");
var model = Environment.GetEnvironmentVariable("OGA_MODEL")
    ?? throw new InvalidOperationException("Set OGA_MODEL to a model name.");
var inputType = args.ElementAtOrDefault(0) ?? "command";
var request = args.ElementAtOrDefault(1) ?? "Move two steps east, then report your position.";

using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
var provider = new OpenAICompatibleProvider(new OpenAICompatibleProviderOptions(
    httpClient,
    new Uri(endpointText))
{
    ApiKey = Environment.GetEnvironmentVariable("OGA_API_KEY"),
});

var world = new ExampleWorld();
var dispatcher = new DurableGameActionDispatcher(
    new InMemoryGameActionJournal(),
    world);
var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, model)
{
    Instructions =
        "You control one game actor. The supplied world state is authoritative. "
        + "Use a tool for every world mutation and explain the observed result briefly.",
    ContextProvider = new ExampleContextProvider(world),
    ToolProvider = (input, _) => new ValueTask<IReadOnlyList<AgentTool>>(
        string.Equals(input.Type, "command", StringComparison.Ordinal)
            ? new[]
            {
                GameActionTool.Create(
                    input,
                    "move_actor",
                    "Move the active actor by a bounded two-dimensional delta.",
                    """
                    {
                      "type": "object",
                      "properties": {
                        "dx": { "type": "number", "minimum": -5, "maximum": 5 },
                        "dy": { "type": "number", "minimum": -5, "maximum": 5 }
                      },
                      "required": ["dx", "dy"],
                      "additionalProperties": false
                    }
                    """,
                    dispatcher,
                    conflictKey: _ => input.ActorId,
                    expectedRevision: world.Revision),
            }
            : Array.Empty<AgentTool>()),
    AgentLimits = new AgentLimits
    {
        MaxTurns = 6,
        MaxTotalTokens = 16_000,
        MaxConcurrentTools = 4,
    },
});

var input = new GameInput(
    sessionId: "example-save",
    actorId: "scout",
    type: inputType,
    payloadJson: JsonSerializer.Serialize(new { request, selectedTarget = (string?)null }),
    moment: new GameMoment("example-world", tick: 120, """{"day":3,"hour":8.5}"""),
    inputId: Guid.NewGuid().ToString("N"));

var result = await runtime.RunAsync(
    input,
    (_, agentEvent, _) =>
    {
        if (agentEvent.ModelEvent?.Delta is { Length: > 0 } delta)
        {
            Console.Write(delta);
        }

        return default;
    });

Console.WriteLine();
Console.WriteLine($"Status: {result.Status}; revision: {result.SessionRevision}");
Console.WriteLine("World: " + JsonSerializer.Serialize(world.Snapshot()));

internal sealed class ExampleContextProvider : IGameContextProvider
{
    private readonly ExampleWorld _world;

    public ExampleContextProvider(ExampleWorld world)
    {
        _world = world;
    }

    public ValueTask<IReadOnlyList<GameContextSlice>> GetContextAsync(
        GameInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(new[]
        {
            new GameContextSlice("visible-world", JsonSerializer.Serialize(_world.Snapshot()), priority: 100),
            new GameContextSlice(
                "local-weather",
                """{"condition":"rain","temperature":12.5,"visibility":0.7}""",
                priority: 40),
        });
    }
}

internal sealed class ExampleWorld : IGameActionHandler
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GameActionReceipt> _operations = new(StringComparer.Ordinal);
    private double _x = 10.5;
    private double _y = -2.25;
    private long _revision = 7;

    public long Revision
    {
        get
        {
            lock (_gate) return _revision;
        }
    }

    public object Snapshot()
    {
        lock (_gate) return new { actor = "scout", x = _x, y = _y, stateRevision = _revision };
    }

    public ValueTask<GameActionReceipt> ExecuteAsync(
        GameActionIntent intent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_operations.TryGetValue(intent.OperationId, out var existing))
            {
                return new(existing);
            }

            if (intent.ExpectedRevision is { } expected && expected != _revision)
            {
                return new(GameActionReceipt.Rejected(
                    intent,
                    "stale_world",
                    "The world changed before this action could commit."));
            }

            using var arguments = JsonDocument.Parse(intent.ArgumentsJson);
            var dx = arguments.RootElement.GetProperty("dx").GetDouble();
            var dy = arguments.RootElement.GetProperty("dy").GetDouble();
            if (Math.Abs(dx) > 5 || Math.Abs(dy) > 5)
            {
                return new(GameActionReceipt.Rejected(intent, "out_of_range", "Movement is limited to five units per action."));
            }

            _x += dx;
            _y += dy;
            _revision++;
            var receipt = GameActionReceipt.Committed(
                intent,
                JsonSerializer.Serialize(new { x = _x, y = _y }),
                _revision);
            _operations.Add(intent.OperationId, receipt);
            return new(receipt);
        }
    }

    public ValueTask<GameActionReceipt?> RecoverAsync(
        GameActionIntent intent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return new(_operations.TryGetValue(intent.OperationId, out var receipt) ? receipt : null);
        }
    }
}
