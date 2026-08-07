# Getting started

The fastest path is to run the buildable console example, then replace its context and action handler with game code.

## Requirements

- .NET SDK 8.0
- an OpenAI-compatible chat-completions endpoint with streaming tool calls
- a model that supports the behavior your game exposes

No model is installed by this repository. The endpoint may be a cloud service or a local server.

## Run the example

Set environment variables in the current shell:

```powershell
$env:OGA_MODEL_ENDPOINT = 'https://your-provider.example/v1/chat/completions'
$env:OGA_MODEL = 'your-model'
$env:OGA_API_KEY = 'your-key'
dotnet run --project examples/OpenGameAgent.Example
```

Use `chat` as the first argument for a quick response or `command` to expose a world-changing movement tool:

```powershell
dotnet run --project examples/OpenGameAgent.Example -- chat "What is near you?"
dotnet run --project examples/OpenGameAgent.Example -- command "Move two steps east"
```

The example supplies numeric position data, streams output, journals the movement intent, validates it in game code, commits the state, and returns a receipt.

## Build a runtime

Create one provider and one long-lived runtime. Do not create a new `HttpClient` for every input.

```csharp
var providerOptions = new OpenAICompatibleProviderOptions(
    httpClient,
    new Uri(modelEndpoint))
{
    GetApiKeyAsync = _ => new(Environment.GetEnvironmentVariable("MODEL_API_KEY"))
};

var options = new GameAgentRuntimeOptions(
    new OpenAICompatibleProvider(providerOptions),
    modelName)
{
    Instructions = "Use supplied state as truth. Mutations require tools.",
    ContextProvider = contextProvider,
    ToolProvider = toolProvider,
    SessionStore = new FileGameSessionStore(sessionDirectory),
    Limits = new GameRuntimeLimits
    {
        MaxConcurrentActors = 16,
        MaxQueuedInputsPerActor = 32
    }
};

var runtime = new GameAgentRuntime(options);
```

`GameAgentRuntimeOptions` is snapshotted by the constructor. Build a new runtime to deploy a different model, prompt, tool set, or limit policy.

## Supply context

Implement `IGameContextProvider`. Return only data this actor is allowed to observe, and include versions when they help the model reason about freshness.

```csharp
public ValueTask<IReadOnlyList<GameContextSlice>> GetContextAsync(
    GameInput input,
    CancellationToken cancellationToken)
{
    var visibleState = world.Observe(input.ActorId);
    return new(new[]
    {
        new GameContextSlice("visible-world", JsonSerializer.Serialize(visibleState), priority: 100),
        new GameContextSlice("actor-state", JsonSerializer.Serialize(world.Actor(input.ActorId)), priority: 90)
    });
}
```

Context is treated as data, not as a hidden state mutation channel.

## Expose actions

Create tools per input so they can carry the stable input identity and actor scope. Prefer `GameActionTool.Create` for state changes.

```csharp
var dispatcher = new DurableGameActionDispatcher(actionJournal, gameActionHandler);

ValueTask<IReadOnlyList<AgentTool>> Tools(GameInput input, CancellationToken _)
{
    var move = GameActionTool.Create(
        input,
        "move_actor",
        "Move the active actor by a bounded delta.",
        """{"type":"object","properties":{"dx":{"type":"number"},"dy":{"type":"number"}},"required":["dx","dy"],"additionalProperties":false}""",
        dispatcher,
        conflictKey: _ => input.ActorId);
    return new(new[] { move });
}
```

`IGameActionHandler.ExecuteAsync` must recheck visibility, permission, resources, expected revision, and all game rules. Return `Rejected` for a legal request that cannot commit. Implement `RecoverAsync` using the game's operation ledger or transaction log.

## Select routes

The default route is intentionally simple:

- explicit `agent.route` metadata wins;
- a configured input-type route wins next;
- an optional model classifier may choose;
- available tools or pending work choose `Agent`;
- otherwise choose `QuickResponse`.

Quick response still calls the model but stops after one turn and exposes no tools. Use type routes for predictable latency:

```csharp
options.RoutePolicy = new AutomaticGameRoutePolicy(new Dictionary<string, GameRouteDecision>
{
    ["ambient_chat"] = GameRouteDecision.Quick("ambient-chat"),
    ["monthly_simulation"] = GameRouteDecision.ToWorkflow("monthly", "fixed-simulation")
});
```

## Add memory and skills

Memory is not injected automatically. Search it in your context provider, enforce game-time and visibility filters, then return selected results as a context slice. Wrap a store in `RankedGameMemoryStore` when you have a domain-specific or embedding-based ranker.

Skills are instruction-only packages. Use `InMemoryGameSkillSource` or `DirectoryGameSkillSource`. A directory skill can be a portable `SKILL.md`:

```markdown
---
name: safe-building
description: Plan and validate world construction.
---
Inspect the region and estimate resources before placing a blueprint.
```

For game-specific selection, use `skill.json` with `id`, `name`, optional `inputTypes`, `toolNames`, `priority`, and `instructionsFile` fields. The default instruction file is `instructions.md`. The `SKILL.md` loader supports scalar front matter; use the JSON manifest when richer metadata is needed.

After any tool turn, the runtime refreshes game context, tools, and selected skills before asking the model to continue. Set `RefreshContextAfterToolTurns = false` only when a game supplies immutable turn context or implements replacement context in `AgentHooks.PrepareNextTurnAsync`.

## Steer or abort an active actor

Long autonomous actions can receive urgent structured observations without starting a second run for the same actor:

```csharp
var key = new GameSessionKey("save-42", "npc-blacksmith");
runtime.TrySteer(key, AgentMessage.UserJson("""{"threat":{"distance":2.5}}"""));
runtime.TryAbort(key); // best-effort cancellation if the actor is still active
```

`TrySteer` and `TryAbort` return `false` when the actor is idle. Steering is consumed at a safe loop boundary after the current tool batch.

## Inspect output

Subscribe to runtime events for UI streaming, tool progress, and traces. Final transcript messages are stored in the session store. `GameAgentRunResult` reports route, status, session revision, kernel result, usage, and error.

Do not mutate engine objects from model-provider continuation threads. The Godot and Unity adapters marshal their public callbacks onto the engine thread; custom action handlers must do the same before touching engine APIs.
