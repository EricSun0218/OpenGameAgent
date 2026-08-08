# Getting started

The fastest path is to run the buildable console example, then replace its context and action handler with game code.

## Requirements

- .NET SDK 8.0
- for the console example, an OpenAI-compatible chat-completions endpoint with streaming tool calls
- a model that supports the behavior your game exposes

No model is installed by this repository. The endpoint may be a cloud service or a local server. Native provider packages and the bundled multi-provider directory are covered later in this guide.

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

For a composition that third-party packages can extend, use the one-shot builder:

```csharp
var runtime = new GameAgentBuilder(provider, modelName)
    .UseInstructions("Use supplied game state as truth. Mutations require tools.")
    .UseSessionStore(new FileGameSessionStore(sessionDirectory))
    .UseExtension(new ToolPolicyExtension(new[] { gamePolicy }))
    .UseExtension(new GameMemoryExtension(memoryStore, recallQueryFactory))
    .Configure(options =>
    {
        options.ContextProvider = contextProvider;
        options.ToolProvider = toolProvider;
    })
    .Build();
```

The builder can only build once. Extensions can contribute prompt fragments, context, tools, skills, routes, workflows, hooks, providers, services, and typed lifecycle handlers without changing the kernel. Register optional features this way rather than adding them to every run.

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

The directory loader scans nested skill folders, rejects paths that escape the selected skill directory, and loads instructions only for selected skills. Imported instructions are untrusted content; they do not install code or grant tool permission.

After any tool turn, the runtime refreshes game context, tools, and selected skills before asking the model to continue. Set `RefreshContextAfterToolTurns = false` only when a game supplies immutable turn context or implements replacement context in `AgentHooks.PrepareNextTurnAsync`.

## Keep large catalogs and outputs out of context

Use `ToolCatalogExtension` for game-owned catalogs that are too large to expose on every turn. Use `McpToolConnectorExtension` for external tool servers; its default `OnDemand` mode exposes one fixed search/describe/call tool and connects only when the model invokes it. Choose `GameMcpToolExposure.Direct` only for a small trusted catalog whose native schemas should always be visible.

Use `ArtifactExtension` when tools can return large text or JSON. Results above its configured threshold are saved by `IGameAgentArtifactStore` and replaced inline with a bounded artifact handle and preview. The model can retrieve the artifact when it actually needs the full value. This preserves the canonical result while preventing one observation from consuming the remaining context window.

## Choose models and credentials

`OpenGameAgent.Models` describes input/output capabilities, context and output limits, reasoning levels, availability, and cost separately from the core provider interface. A `GameModelCatalog` can combine static and dynamically refreshed local or remote providers and resolve a compatible model for a run.

Install `OpenGameAgent.Models.BuiltIn` when the game should use the bundled provider/model directory instead of constructing one low-level adapter itself. Configure a credential first; the example below reads `OPENAI_API_KEY` from the environment by default:

```csharp
using OpenGameAgent.Models.BuiltIn;

var modelRuntime = new BuiltInGameModelRuntime(
    new BuiltInGameModelRuntimeOptions(httpClient));
var available = await modelRuntime.Catalog.GetAvailableModelsAsync("openai");
var selected = available.First();
var provider = modelRuntime.CreateProvider("openai");
var agent = new Agent(new AgentOptions(provider, selected.ModelId));
```

Availability checks use the configured authentication chain, so a provider with no usable credentials is not presented as ready. A game may select by required input/output capability and reasoning level rather than taking the first result. Direct provider packages remain appropriate when a title intentionally supports only one endpoint.

Authentication is replaceable: static credentials, environment resolution, game-owned credential stores, or local/no-auth providers can share the same catalog. If the developer pays for inference, use `DeveloperGatewayProvider` to obtain short-lived scoped access from the developer's authenticated gateway. Never ship a permanent upstream provider key in a client build.

`OpenGameAgent.Models.Auth.BuiltIn` registers optional browser and device flows against the same credential store. Client IDs are developer configuration, not framework defaults; a flow that requires one stays disabled until it is explicitly supplied. Use an encrypted platform credential store in a shipped product—the included in-memory store is for composition and tests, not durable secret protection.

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
