<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/brand/opengameagent-mark.svg">
    <source media="(prefers-color-scheme: light)" srcset="docs/brand/opengameagent-mark-light.svg">
    <img src="docs/brand/opengameagent-mark-light.svg" alt="OpenGameAgent OGA monogram" width="112">
  </picture>
</p>

<h1 align="center">OpenGameAgent</h1>

<p align="center"><strong>Open-source agent runtime for AI-native games, autonomous NPCs, and interactive worlds.</strong></p>

<p align="center"><a href="README.zh-CN.md">简体中文</a></p>

OpenGameAgent is a compact, hackable C# runtime that lets game characters observe structured state, plan, call tools, inspect authoritative results, remember, and continue toward goals. It runs inside Godot, Unity, or .NET services while game code remains authoritative over every state change.

<p align="center">
  <a href="https://github.com/EricSun0218/OpenGameAgent/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/EricSun0218/OpenGameAgent/actions/workflows/ci.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-blue.svg"></a>
  <a href="CHANGELOG.md"><img alt="Status: alpha" src="https://img.shields.io/badge/status-alpha-orange.svg"></a>
</p>

OpenGameAgent starts with a small, composable agent kernel. The stateful core streams model output, executes validated tools, accepts steering while running, and continues the model/tool loop until work is complete. Use the kernel by itself, add the game runtime for game time and durable state, then opt into extensions for memory, goals, host-verified task plans, artifacts, delegation, external tools, structured interaction, and workflow graphs.

Inputs are bounded JSON plus optional durable image observations. They may represent dialogue, combat observations, simulation ticks, UI events, plans, sensor state, screenshots, or any other game-owned data; natural language is optional. No model is bundled. Cloud and local API endpoints are both supported.

## A programmable agent runtime built for games

Developers choose a model, register game-owned tools, compose extensions, stream events, steer an active run, and let the model/tool loop continue until the task is finished. The kernel remains useful on its own as a general agent loop. The optional game runtime adds the coordinates an interactive simulation needs: sessions and actors, game timelines and ticks, structured world context, bounded multi-NPC concurrency, persistent memory and tasks, engine-thread handoff, and durable action receipts. These capabilities are composed through typed interfaces instead of being hard-coded into one genre or world model.

## Beyond dialogue: observe, decide, act, and continue

A dialogue-only character receives a prompt and returns a line. An agent-driven character receives a goal and the current environment, chooses tools, performs work, observes the result, and continues until it reaches a terminal outcome. In OpenGameAgent, those tools are ordinary game-owned operations: move, inspect, trade, build, schedule, recruit, investigate, or any other capability the developer exposes.

| Dialogue-only character | Agent-driven character |
| --- | --- |
| Reads the latest dialogue | Observes bounded JSON containing dialogue, world state, events, UI input, sensor data, or simulation ticks |
| Produces the next line of text | Streams text and typed tool calls, then consumes structured tool results |
| Stops after one model response | Can observe → decide → act → inspect the result → continue across multiple turns |
| Treats generated text as the outcome | Requests actions while game code validates permissions, rules, revisions, and state changes |
| Usually follows wall-clock chat history | Can reason against game time, timelines, save/session identity, actor identity, and scoped memory |
| Models one conversation at a time | Serializes each actor while allowing bounded concurrency across many NPCs |
| May repeat a write after a timeout | Can journal state-changing intents and reconcile authoritative receipts before retrying |

OpenGameAgent is model- and provider-neutral. Characters operate through developer-defined tools; a model never directly controls game state. The game remains authoritative at every mutation boundary.

Not every interaction needs the full loop. A game can route greetings and other simple inputs through the quick-response path, use the complete agent loop for open-ended tasks, and use deterministic workflows where the execution graph should be fixed.

> Current version: `0.3.0-alpha.2`. Public APIs can change before `1.0`.

The kernel boundary is intentionally small and designed to stabilize early. New game-specific capabilities should normally arrive as extensions, tools, policies, workflows, or game-owned services instead of expanding the model/tool loop.

## Install

Download versioned package artifacts, Godot and Unity archives, or the portable server from the [Releases](https://github.com/EricSun0218/OpenGameAgent/releases) page. For active development against a source checkout, reference only the projects your game needs:

```xml
<ItemGroup>
  <ProjectReference Include="path/to/OpenGameAgent/src/OpenGameAgent/OpenGameAgent.csproj" />
  <ProjectReference Include="path/to/OpenGameAgent/src/OpenGameAgent.Memory/OpenGameAgent.Memory.csproj" />
  <ProjectReference Include="path/to/OpenGameAgent/src/OpenGameAgent.Attachments.Local/OpenGameAgent.Attachments.Local.csproj" />
</ItemGroup>
```

Adjust the paths relative to your game project and omit optional projects you do not use. The kernel, persistence, providers, memory, attachments, plugins, and engine-compatible client are also shipped as separate `OpenGameAgent.*` release artifacts. See [Getting started](docs/getting-started.md) and [Engine integration](docs/engine-integration.md) before connecting a game.

## Why game-specific?

General agent loops commonly assume one user, wall-clock time, and a linear conversation. A game may have multiple timelines, save forks, thousands of actors, offline time jumps, engine main-thread constraints, and actions that must never be repeated after an uncertain failure.

OpenGameAgent keeps the reusable agent machinery independent from the game while exposing the coordinates games need:

- named timelines and integer ticks, with optional calendar JSON;
- structured observations and context slices with floating-point values intact;
- content-addressed screenshot/image input with decode validation, model-capability preflight, and session-authorized retrieval;
- quick-response, full-agent, and deterministic-workflow routes;
- per-actor serialization with bounded cross-actor concurrency;
- journaled action intents and authoritative game receipts;
- game-time memory filtering, expiry, and optional custom ranking;
- optional local/remote embeddings, rebuildable vector indexes, and lexical/vector hybrid recall;
- skills selected by input type and available tools;
- recurring game-time triggers and persistent actor mailboxes with payload-free backlog queries;
- a typed extension API for tools, skills, routes, workflows, hooks, events, and services;
- capability-aware model catalogs and developer-hosted short-lived credentials;
- lazy external-tool discovery and large-result artifact spill;
- Agent Plugins 1.0.0 packages containing portable skills and MCP servers;
- image, audio, and video generation through replaceable APIs.
- append-only traces, observation-only playback, and offline CI evaluation.
- realtime speech with barge-in, background-agent handoff, and replaceable presentation behaviors.

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
| Realtime conversation | Bounded PCM16 streaming, live transcription/audio events, subtitle timing, barge-in cancellation/truncation, non-blocking background-agent handoff/steering, and cancel-replace presentation behaviors |
| Image input | PNG/JPEG/WebP/GIF admission, immutable content-addressed storage, reference-only transcripts, capability preflight, tool-result images, and authorized server retrieval |
| Extension API | Immutable builder; prompt/context/tool/skill/route/workflow/hook/provider/service registration; typed lifecycle events and channels; namespaced persistent state |
| Official extensions | Tool policy and search, structured player questions/recommended replies, goals, host-verified ordered task plans with durable pause/resume, memory, artifacts, knowledge, delegation, tracing, and durable parallel workflow graphs |
| DevTools | Bounded JSONL recordings, local observation-only HTML playback, summaries, and offline/CI evaluation rules |
| World primitives | Durable actions, bounded engine-thread action handoff, resumable workflows, memories, skills, signals, game-time schedules, actor mailboxes with batch read-only pending status |
| Models and auth | Bundled capability/context/reasoning/cost directory, dynamic refresh, API-key/environment/stored/OAuth/local auth, developer-hosted short-lived credential gateway |
| External tools | Lazy on-demand search/describe/call by default; explicit direct exposure for small trusted catalogs |
| Portable plugins | [Agent Plugins 1.0.0](docs/agent-plugins.md) `plugin.json`, immediate-child `SKILL.md` discovery, MCP stdio/Streamable HTTP, client namespaces, containment, and component-level failure isolation |
| Providers | Native Anthropic, Amazon Bedrock, Google Gemini/Vertex, Mistral, OpenAI Responses/Azure, OpenAI-compatible, OpenAI Realtime, Volcengine realtime speech, remote gateway, and message-gateway transports; retry/fallback decorators |
| Generated media | Provider-neutral image/audio/video registry, generic async HTTP jobs, OpenRouter previews, official OpenAI Images, and Volcengine Ark/Seedream image generation and editing |
| Persistence | Crash-tolerant local snapshots plus optional append-only session history, cross-process coordination, action journals, workflow checkpoints, memories, mailboxes, artifacts, delegations, skills, and prompt templates |
| Semantic memory | Optional model-agnostic embeddings, authoritative-save verification, rebuildable local vector index, hybrid lexical/vector recall, structured diagnostics, and game-time reranking |
| Placement | Shared `netstandard2.1` runtime in Godot, Unity, or another C# host; optional .NET 8 HTTP/SSE service with C# and native C++ clients |
| Engines | Godot 4.7 .NET and Unity 6 in-process packages; Unreal Engine 5.8 native C++ sidecar plugin |

Realtime speech is an optional layer rather than a second game-authority path. The realtime transport can converse or transcribe, and can request reversible gaze, gesture, expression, or movement presentation. Planning and durable world mutations are handed to the same `GameAgentRuntime` and game-owned tools used by non-voice inputs. Optional OpenAI and Volcengine adapters share this contract; the Volcengine adapter keeps dialogue/VAD and streaming TTS separate from the authoritative agent loop. See [Realtime conversations](docs/realtime-conversations.md).

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

- **Inside a C# engine host:** simplest single-player deployment for Godot .NET or Unity, with direct access to game context and no extra agent server. Suitable for BYOK or local endpoints. A provider key shipped in a client can be extracted.
- **In the game server:** best when the game already has an authoritative server. Run the same C# runtime beside game rules and persistence.
- **Separate agent service:** useful for Unreal, centrally paid inference, secrets, scaling, or independent updates. C# and native engine adapters call `OpenGameAgent.Server` over JSON/SSE and can steer or abort an active actor through authenticated control endpoints.

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
./engines/unreal/test-package.ps1
./engines/unreal/test-plugin.ps1 -UnrealRoot <UE_5.8>
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
- [Image input and game perception](docs/image-input.md)
- [Traces, playback, and offline evaluation](docs/devtools.md)

## Project boundary

This repository is a developer framework. It does not define a universal character sheet, combat model, world-package format, visual editor, or end-user game. Those belong to each game. The framework provides the agent loop and game-aware primitives needed to build conversational characters, autonomous companions, social simulations, AI directors, generated quests and items, strategy agents, construction agents, and persistent interactive worlds.

## Attribution

Every distributed game, mod, application, or product that includes OpenGameAgent must make the OpenGameAgent copyright and MIT license notice available in Credits, About, Third-party Licenses, documentation, or an accompanying license file. The following concise credit may be used alongside the license notice: **“Powered by OpenGameAgent | opengameagent.com”**. See the [license](LICENSE) and [brand assets and attribution guidance](docs/brand/README.md).

## License

[MIT License](LICENSE). You may build proprietary games and hosted products with the framework. Distributed copies must include the copyright and permission notice. See [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md).
