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

The snippet above is the explicit low-level path for an endpoint whose wire contract the host controls. `OpenAICompatibleProvider` cannot safely infer compatibility from an arbitrary model name or URL. For a known hosted provider such as DeepSeek, prefer the bundled model directory shown under **Choose models and credentials**; it applies the model's token field, thinking format, tool constraints, and other compatibility flags to both routing and main requests.

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

Tool visibility and tool execution authorization are separate boundaries. A visibility policy runs
after static and dynamic tools are collected but before each model request, so a disabled tool is not
advertised to the model:

```csharp
.UseExtension("game.tool-visibility", "1", api =>
    api.RegisterToolVisibilityPolicy("player-settings", (context, cancellationToken) =>
    {
        var disabled = settings.GetDisabledTools(
            context.Input.SessionId,
            context.Input.ActorId,
            context.Input.Type);
        return new ValueTask<bool>(!disabled.Contains(context.Tool.Name));
    }))
```

The policy sees the current `GameInput`, the stable `ToolDefinition`, risk, and contributor ID.
All registered visibility policies must allow a tool. An exception stops the run before provider
dispatch. Keep `ToolPolicyExtension` or equivalent game authority checks as well: hiding a schema
reduces model access but does not authorize a later call.

`GameMemoryExtension` also accepts independent `rememberToolVisibility` and
`searchToolVisibility` predicates. This lets one runtime expose both tools during ordinary inputs,
hide both for a specialized input, or honor per-player settings without duplicating the agent loop.

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

## Add image observations

For screenshots or visual tool results, mount an `IGameImageAttachmentStore` and pass inline image bytes through `GameInput.Content`. The runtime validates and persists the whole batch, saves only immutable references in the transcript, preflights the selected model, and resolves bytes just before provider dispatch. Reference `src/OpenGameAgent.Attachments.Local/OpenGameAgent.Attachments.Local.csproj` from a source checkout, or use its matching package artifact from the GitHub Release.

```csharp
using OpenGameAgent.Attachments;
using OpenGameAgent.Attachments.Local;

options.ImageAttachments = new FileGameImageAttachmentStore(attachmentDirectory);

var input = new GameInput(
    "save-42",
    "npc-scout",
    "scene_changed",
    """{"region":"north-gate"}""",
    new GameMoment("main", 900),
    "scene-900-scout",
    content: new AgentContent[]
    {
        new BinaryContent(
            AgentMediaKind.Image,
            Convert.ToBase64String(pngBytes),
            GameImageMediaTypes.Png,
            "scout-view.png"),
    });
```

Use a model whose catalog entry declares image input. Models that cannot consume the image fail explicitly; the runtime never drops it silently. For large voxel or open worlds, combine bounded structured state, a sparse BEV/topological summary, selective screenshots, and exact query tools instead of serializing every coordinate. See [Image input and game perception](image-input.md).

## Expose actions

Create tools per input so they can carry the stable input identity and actor scope. Prefer `GameActionTool.Create` for state changes.

Pass an explicit, game-owned `inputId` to `GameInput` when an input can be retried after a disconnect or process restart. The optional constructor fallback creates a fresh unique ID; it cannot identify the same logical input in a later process.

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

When the model loop runs off the engine thread, wrap that authoritative handler in
`QueuedGameActionHandler` and pump it from the engine thread. The durable dispatcher remains the
only action journal; the queue is a bounded, process-local handoff:

```csharp
var engineActions = new QueuedGameActionHandler(
    new AuthoritativeGameActionHandler(world),
    maximumPendingActions: 256,
    maximumActiveActions: 16);
var dispatcher = new DurableGameActionDispatcher(actionJournal, engineActions);

// Unity Update, Godot _Process, or the equivalent host-owned main-thread callback.
engineActions.Pump(maximumWorkItems: 16);
```

The first `Pump` call binds the instance to that managed thread. Cancellation removes an action
only while it is still queued. Once the host starts an action, caller timeout no longer cancels the
mutation blindly; its receipt is allowed to settle. `Stop` rejects new work and faults queued work,
while `DisposeAsync` additionally waits for already-started work. The wrapped authoritative handler
must validate `GenerationId` against the currently loaded save/world generation because only the
game knows which generation is active. Pending durable journal entries are recovered through the
same pump after restart.

## Select routes

The default route is intentionally simple:

- explicit `agent.route` metadata wins;
- a configured input-type route wins next;
- pending work chooses `Agent` without a classifier call;
- an optional model classifier can still select `QuickResponse` for ordinary input when tools are available;
- without a classifier, available tools conservatively choose `Agent`;
- otherwise choose `QuickResponse`.

Supported explicit values are `auto`, `quick`, `agent`, `direct`, `plan`, and `workflow:<name>`. `direct` selects the short-task Agent loop while hiding official persistent-plan tools; `plan` keeps that loop and adds persistent-plan guidance. Quick response still calls the answer model but stops after one turn and exposes no tools. Use type routes for predictable latency:

```csharp
options.RoutePolicy = new AutomaticGameRoutePolicy(new Dictionary<string, GameRouteDecision>
{
    ["ambient_chat"] = GameRouteDecision.Quick("ambient-chat"),
    ["monthly_simulation"] = GameRouteDecision.ToWorkflow("monthly", "fixed-simulation")
});
```

If you configure `ModelGameRouteClassifier`, its provider call is recorded under `GameSessionUsageCause.Routing` and shares the same per-input model-token budget as the selected route. The runtime never runs a speculative Quick answer and then replays it as Agent work. See [Execution routing and performance](execution-routing-and-performance.md).

## Add memory and skills

Memory is not injected automatically. Search it in your context provider, enforce game-time and visibility filters, then return selected results as a context slice. Wrap a store in `RankedGameMemoryStore` for a custom ranker. For model-agnostic local or remote embeddings, a rebuildable derived vector index, and lexical/vector hybrid recall, use the optional `OpenGameAgent.Memory` package described in [Hybrid and vector memory](memory.md).

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

To consume a portable Agent Plugins 1.0.0 package instead of a standalone skill directory, reference `src/OpenGameAgent.Plugins/OpenGameAgent.Plugins.csproj` from a source checkout, or use its matching package artifact from the GitHub Release, then load the package as one runtime extension:

```csharp
using OpenGameAgent.Plugins;

var plugin = AgentPluginLoader.Load(
    pluginDirectory,
    new AgentPluginLoadOptions
    {
        // Required only when the package contains stdio MCP servers.
        PluginDataDirectory = pluginDataDirectory,
    });

await using var runtime = new GameAgentBuilder(provider, model)
    .UseExtension(plugin)
    .Build();
```

The adapter validates `plugin.json`, discovers only immediate `skills/*/SKILL.md` children, and maps valid `mcp.json` stdio and Streamable HTTP entries to the existing MCP connector. Invalid skills and MCP server entries are diagnosed independently. The optional legacy SSE MCP transport is reported and skipped. Package paths cannot escape the plugin root, `${PLUGIN_ROOT}` and `${PLUGIN_DATA}` expansion is single-pass, and the default HTTP transport disables redirects so visible package headers do not cross origins. See [Agent Plugins](agent-plugins.md) for the complete boundary.

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

For a protected DeepSeek key and trusted endpoint override, configure the existing directory-backed factory directly:

```csharp
var options = new BuiltInGameModelRuntimeOptions(httpClient)
{
    GetEnvironmentVariable = _ => null
};
options.Authentications["deepseek"] = new StaticGameProviderAuthentication(
    credential: new GameCredential(GameCredentialKind.ApiKey, apiKey));
options.ProviderConfigurations["deepseek"] = new GameModelProviderTransportConfiguration
{
    BaseUrl = trustedEndpoint
};

var models = new BuiltInGameModelRuntime(options);
var provider = models.CreateProvider("deepseek");
var classifier = new ModelGameRouteClassifier(provider, "deepseek-v4-pro");
var runtimeOptions = new GameAgentRuntimeOptions(provider, "deepseek-v4-pro");
```

Use the same directory-backed provider and model for the classifier and runtime. Credentials stay inside the authentication boundary and are not placed in prompts, transcripts, traces, or provider diagnostics.

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
