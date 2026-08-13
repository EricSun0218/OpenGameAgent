# OpenGameAgent

[简体中文](README.zh-CN.md)

**The agent kernel for games.** A compact, hackable C# runtime for building AI-native games, autonomous NPCs, and interactive worlds in Godot, Unity, or .NET services.

[![CI](https://github.com/EricSun0218/OpenGameAgent/actions/workflows/ci.yml/badge.svg)](https://github.com/EricSun0218/OpenGameAgent/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![Status](https://img.shields.io/badge/status-alpha-orange.svg)](CHANGELOG.md)

OpenGameAgent brings the small, composable agent-kernel model to game development. Its stateful core streams model output, executes validated tools, accepts steering while running, and continues the model/tool loop until work is complete. Use that kernel by itself, add the game layer for game time and durable state, then opt into extension packages for memory, goals, host-verified task plans, artifacts, delegation, external tools, structured interaction, and workflow graphs.

Inputs are bounded JSON. They may represent dialogue, combat observations, simulation ticks, UI events, plans, sensor state, or any other game-owned data; natural language is optional. No model is bundled. Cloud and local API endpoints are both supported.

> Current version: `0.3.0-alpha.2`. Public APIs can change before `1.0`.

The kernel boundary is intentionally small and designed to stabilize early. New game-specific capabilities should normally arrive as extensions, tools, policies, workflows, or game-owned services instead of expanding the model/tool loop.

## Install

Install the complete game runtime from NuGet:

```bash
dotnet add package OpenGameAgent --version 0.3.0-alpha.2
dotnet add package OpenGameAgent.Memory --version 0.3.0-alpha.2 # optional semantic memory
```

The kernel, persistence, providers, and engine-compatible client are also published as separate `OpenGameAgent.*` packages. Godot, Unity, and portable server archives are available on the [Releases](https://github.com/EricSun0218/OpenGameAgent/releases) page. See [Getting started](docs/getting-started.md) and [Engine integration](docs/engine-integration.md) before connecting a game.

## Why game-specific?

General agent loops commonly assume one user, wall-clock time, and a linear conversation. A game may have multiple timelines, save forks, thousands of actors, offline time jumps, engine main-thread constraints, and actions that must never be repeated after an uncertain failure.

OpenGameAgent keeps the reusable agent machinery independent from the game while exposing the coordinates games need:

- named timelines and integer ticks, with optional calendar JSON;
- structured observations and context slices with floating-point values intact;
- quick-response, full-agent, and deterministic-workflow routes;
- per-actor serialization with bounded cross-actor concurrency;
- journaled action intents and authoritative game receipts;
- game-time memory filtering, expiry, and optional custom ranking;
- optional local/remote embeddings, rebuildable vector indexes, and lexical/vector hybrid recall;
- skills selected by input type and available tools;
- recurring game-time triggers and persistent actor mailboxes;
- a typed extension API for tools, skills, routes, workflows, hooks, events, and services;
- capability-aware model catalogs and developer-hosted short-lived credentials;
- lazy external-tool discovery and large-result artifact spill;
- Agent Plugins 1.0.0 packages containing portable skills and MCP servers;
- image, audio, and video generation through replaceable APIs.

The runtime does **not** decide combat legality, inventory rules, economy changes, NPC permissions, or other business rules. The game exposes narrow tools, validates every requested mutation, performs it on the correct thread or server, and returns the authoritative receipt.

## Architecture

```text
Godot / Unity / .NET game server
        |
        | GameInput (bounded JSON + GameMoment)
        v
GameAgentRuntime
  context | skills | route | session | actor lane | extensions
        |
        v
small stateful Agent kernel <---- steering / follow-up
  model stream -> tool calls -> tool results -> next turn
        |                              |
        |                              v
        |                    durable action dispatcher
        |                              |
        v                              v
model API                    game-owned validation + state
```

The kernel can also be used directly when a developer needs only a compact agent loop. The higher game runtime is a composition layer, not a required world schema.

Read [Architecture](docs/architecture.md) for the ownership and failure boundaries.

## What is implemented

| Area | Capability |
| --- | --- |
| Agent kernel | Streaming typed messages, tool loop, typed partial tool results, steering, follow-up, hooks, cancellation, strict transcript validation, provider failures as results |
| Tool execution | Provider-request schema preflight plus execution-time validation over a bounded JSON Schema subset, guaranteed result for every accepted call, safe parallel reads, conflict-key serialization, policy blocking/termination, timeouts, uncertain write outcomes |
| Game runtime | Arbitrary JSON input, game clocks/timelines, fast/full/workflow routing, optimistic sessions, duplicate-input protection, actor concurrency, active-run steering/abort |
| Extension API | Immutable builder; prompt/context/tool/skill/route/workflow/hook/provider/service registration; typed lifecycle events and channels; namespaced persistent state |
| Official extensions | Tool policy and search, structured player questions/recommended replies, goals, host-verified ordered task plans, memory, artifacts, knowledge, delegation, tracing, and durable parallel workflow graphs |
| World primitives | Durable actions, resumable workflows, memories, skills, signals, game-time schedules, actor mailboxes |
| Models and auth | Bundled capability/context/reasoning/cost directory, dynamic refresh, API-key/environment/stored/OAuth/local auth, developer-hosted short-lived credential gateway |
| External tools | Lazy on-demand search/describe/call by default; explicit direct exposure for small trusted catalogs |
| Portable plugins | [Agent Plugins 1.0.0](docs/agent-plugins.md) `plugin.json`, immediate-child `SKILL.md` discovery, MCP stdio/Streamable HTTP, client namespaces, containment, and component-level failure isolation |
| Providers | Native Anthropic, Amazon Bedrock, Google Gemini/Vertex, Mistral, OpenAI Responses/Azure, OpenAI-compatible, remote gateway, and message-gateway transports; retry/fallback decorators |
| Generated media | Provider-neutral image/audio/video registry, generic async HTTP jobs, and a dedicated OpenRouter image adapter with progressive previews |
| Persistence | Crash-tolerant local snapshots plus optional append-only session history, cross-process coordination, action journals, workflow checkpoints, memories, mailboxes, artifacts, delegations, skills, and prompt templates |
| Semantic memory | Optional model-agnostic embeddings, authoritative-save verification, rebuildable local vector index, hybrid lexical/vector recall, structured diagnostics, and game-time reranking |
| Placement | Shared `netstandard2.1` runtime in Godot, Unity, or another C# host; optional .NET 8 HTTP/SSE service and engine client |
| Engines | Godot 4.7 .NET and Unity 6 packages, both exercised in real Windows editors |

Run inputs, model content, tool catalogs, loops, queues, progress, and concurrency are bounded by explicit limits. Context admission runs before every model request, model and tool calls have deadlines, and large tool results can be retained as artifacts instead of repeatedly filling the prompt. Game-owned stores and rankers can replace the included in-memory or local-file implementations.

### Model access without hand-wiring every provider

`OpenGameAgent.Models.BuiltIn` turns the bundled model directory into an executable runtime. It currently dispatches nine wire APIs across 27 provider definitions and hundreds of text/tool-capable models, applying provider-specific request formats, reasoning settings, compatibility flags, cost metadata, authentication, cancellation, and bounded response handling. Provider usage is priced from the resolved directory when the provider does not report cost, while unavailable pricing remains explicitly unknown rather than appearing free. The lower provider packages remain independently usable when a game wants an explicit model and endpoint instead of a directory.

`OpenGameAgent.Models.Auth.BuiltIn` adds opt-in browser or device authorization flows for supported subscription providers. Public client registrations are never embedded in the framework: flows that require a client ID remain disabled until the game developer supplies one. `OpenGameAgent.ProviderTransport` exposes only allowlisted, bounded response metadata to observers and never passes credentials or arbitrary response headers to tracing code.

Image, audio, and video generation use a separate model registry because generation jobs, previews, polling, and outputs are not chat completions. The framework ships the neutral registry, a generic HTTP job adapter, and a dedicated image provider; games can register local generators or additional APIs without changing the agent kernel.

## Minimal kernel

```csharp
using OpenGameAgent.Kernel;
using OpenGameAgent.Providers.OpenAICompatible;

var http = new HttpClient();
var provider = new OpenAICompatibleProvider(new(http, endpoint)
{
    ApiKey = Environment.GetEnvironmentVariable("MODEL_API_KEY")
});

var agent = new Agent(new AgentOptions(provider, "your-model")
{
    SystemPrompt = "You are an NPC. Use tools when the world must change."
});

using var events = agent.Subscribe((e, _) =>
{
    if (e.ModelEvent?.Delta is { } delta) Console.Write(delta);
    return default;
});

var result = await agent.RunAsync("What can you see?");
```

## Game runtime

```csharp
var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "your-model")
{
    Instructions = "Act only from supplied game state. Use tools for mutations.",
    ContextProvider = myGameContext,
    ToolProvider = myGameTools,
    SessionStore = mySessionStore
});

var input = new GameInput(
    sessionId: "save-42",
    actorId: "npc-blacksmith",
    type: "player_interaction",
    payloadJson: """{"intent":"repair","item":"sword","durability":0.35}""",
    moment: new GameMoment("main-world", tick: 18840),
    inputId: "interaction-9001");

var run = await runtime.RunAsync(input);
```

See the buildable [living-world example](examples/OpenGameAgent.Example/Program.cs) and [Getting started](docs/getting-started.md).

## Choose where it runs

- **Inside the engine:** simplest single-player deployment, direct access to game context, no extra agent server. Suitable for BYOK or local endpoints. A provider key shipped in a client can be extracted.
- **In the game server:** best when the game already has an authoritative server. Run the same C# runtime beside game rules and persistence.
- **Separate agent service:** useful for centrally paid inference, secrets, scaling, or independent updates. Engine adapters call `OpenGameAgent.Server` over JSON/SSE and can steer or abort an active actor through authenticated control endpoints.

For developer-funded client inference, use a developer-controlled gateway that issues short-lived scoped credentials. The permanent upstream provider key stays on developer infrastructure; the framework supplies the client credential flow, while the game owns login, quotas, revocation, and abuse controls.

Placement does not change ownership: only game code decides whether an action commits.

## Build and verify

Requirements: .NET SDK 8.0. Windows and Linux are supported for the shared runtime and server. Engine adapters currently target Windows editor verification.

```powershell
dotnet restore OpenGameAgent.sln
dotnet build OpenGameAgent.sln -c Release --no-restore
dotnet test OpenGameAgent.sln -c Release --no-build --no-restore
./engines/godot/test-package.ps1 -GodotSharpDir <GodotSharp/Api/Debug>
./engines/godot/test-engine.ps1 -Godot <godot_console.exe> -GodotSharpDir <GodotSharp/Api/Debug>
./engines/unity/test-package.ps1 -UnityManagedDir <Unity/Editor/Data/Managed/UnityEngine>
./engines/unity/test-editor.ps1 -UnityEditor <Unity.exe> -UnityManagedDir <Unity/Editor/Data/Managed/UnityEngine>
```

Real-editor gates are documented in [Engine integration](docs/engine-integration.md).

## Documentation

- [Getting started](docs/getting-started.md)
- [Architecture and authority boundaries](docs/architecture.md)
- [Feature and API map](docs/features.md)
- [Game integration patterns](docs/game-integration-patterns.md)
- [Engine integration](docs/engine-integration.md)
- [Deployment and security](docs/deployment-and-security.md)
- [Generated media](docs/media.md)

## Project boundary

This repository is a developer framework. It does not define a universal character sheet, combat model, world-package format, visual editor, or end-user game. Those belong to each game. The framework provides the agent loop and game-aware primitives needed to build conversational characters, autonomous companions, social simulations, AI directors, generated quests and items, strategy agents, construction agents, and persistent interactive worlds.

## License

[Apache License 2.0](LICENSE). You may build proprietary games and hosted products with the framework. See [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md).
