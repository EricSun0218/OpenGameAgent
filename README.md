<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/brand/opengameagent-mark.svg">
    <source media="(prefers-color-scheme: light)" srcset="docs/brand/opengameagent-mark-light.svg">
    <img src="docs/brand/opengameagent-mark-light.svg" alt="OpenGameAgent AI NPC agent runtime logo" width="112">
  </picture>
</p>

<h1 align="center">OpenGameAgent</h1>

<p align="center"><strong>Build AI NPCs and in-game agents that understand context, run ReAct tool loops, plan complex tasks, and execute reliable actions.</strong></p>

<p align="center"><a href="https://opengameagent.com">Website</a> · <a href="docs/getting-started.md">Getting started</a> · <a href="README.zh-CN.md">简体中文</a></p>

OpenGameAgent is an open-source C# agent runtime for AI NPCs and other agents that operate inside games. It is not an AI coding agent or a game generator. It equips in-game characters and systems with structured-context understanding, ReAct reasoning and tool use, complex-task decomposition, durable planning that adapts to new evidence, and reliable execution through game-owned tools. The game remains authoritative over every state change.

<p align="center">
  <a href="https://github.com/EricSun0218/OpenGameAgent/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/EricSun0218/OpenGameAgent/actions/workflows/ci.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-blue.svg"></a>
  <a href="CHANGELOG.md"><img alt="Status: alpha" src="https://img.shields.io/badge/status-alpha-orange.svg"></a>
</p>

## What is OpenGameAgent?

OpenGameAgent turns a model into a programmable NPC or in-game agent instead of a dialogue endpoint. Developers choose a model, provide structured context, register game-owned tools, compose extensions, stream progress, tool calls, and answers as they are produced, and steer or cancel active work. The compact kernel handles the bounded model/tool loop; the optional game runtime adds sessions, actors, game timelines, memory, plans, multi-NPC scheduling, engine-thread handoff, and durable action receipts.

Every input enters the same Agent loop. If the model returns an assistant message, the turn ends immediately; if it returns tool calls, the runtime executes the authorized tools, appends their structured results, refreshes game context, and continues. There is no preliminary complexity classifier and no separate Quick/Agent/Plan execution engine. Complex tasks can optionally use persistent goals and ordered plans that survive later inputs, wait for game events, preserve completed steps, and replace unfinished work when new evidence invalidates the original approach. Fixed business processes remain ordinary game-owned state machines and tools.

OpenGameAgent runs inside Godot or Unity, behind a native Unreal Engine client, or in a .NET game server or sidecar. Inputs are bounded JSON plus optional durable image observations, so they can represent dialogue, combat state, simulation ticks, UI events, sensor data, screenshots, or any other game-owned context; natural language is optional. No model is bundled, and both cloud and local API endpoints are supported.

## From dialogue to reasoning, planning, and action

A dialogue-only character receives a prompt and returns a line. An AI NPC or in-game agent receives a goal and the current environment, reasons about the next step, uses developer-defined tools, observes authoritative results, and adapts its approach. Those tools are ordinary game operations: move, inspect, trade, build, schedule, recruit, investigate, or any other capability the developer exposes.

| Dialogue-only character | Agent-driven character |
| --- | --- |
| Reads the latest dialogue | Observes bounded JSON containing dialogue, world state, events, UI input, sensor data, or simulation ticks |
| Produces the next line of text | Streams text and typed tool calls, then consumes structured tool results |
| Stops after one model response | Runs a bounded ReAct loop across model responses, tool calls, and structured results |
| Has no durable task model | Can decompose a complex goal into persistent steps and replan unfinished work as conditions change |
| Treats generated text as the outcome | Requests actions while game code validates permissions, rules, revisions, and state changes |
| Usually follows wall-clock chat history | Can reason against game time, timelines, save/session identity, actor identity, and scoped memory |
| Models one conversation at a time | Serializes each actor while allowing bounded concurrency across many NPCs |
| May repeat a write after a timeout | Can journal state-changing intents and reconcile authoritative receipts before retrying |

OpenGameAgent is model- and provider-neutral. Characters operate through developer-defined tools; a model never directly controls game state. The game remains authoritative at every mutation boundary.

Simple dialogue stays fast naturally: the model answers once and the loop ends without executing a tool. Tool-driven work continues in that same loop, while durable goals and task plans are optional extension tools exposed only when the host grants persistent-planning capability.

> Current source pre-release: `0.3.0-alpha.4`. Public APIs can change before `1.0`; pin an immutable tag or source commit for shipped games.

The kernel boundary is intentionally small and designed to stabilize early. New game-specific capabilities should normally arrive as extensions, tools, policies, or game-owned services instead of expanding the model/tool loop.

## Install

Published versioned artifacts remain available on the [Releases](https://github.com/EricSun0218/OpenGameAgent/releases) page. For the current `0.3.0-alpha.4` source line, C# and Godot development should use a pinned source checkout and reference only the projects the game needs.

The release pipeline binds every artifact to one source commit and generates `RELEASE_MANIFEST.json` plus `SHA256SUMS.txt` for verification. Runtime Protocol v1 uses capability negotiation for additive features; changes to required fields, enum meaning, cursor semantics, or lifecycle ordering require a new protocol version.

Unity 6 projects can install the complete `0.3.0-alpha.4` package directly from its immutable GitHub UPM tag:

```text
https://github.com/EricSun0218/OpenGameAgent.git#upm/0.3.0-alpha.4
```

The same release is available through OpenUPM: `openupm add com.opengameagent.runtime@0.3.0-alpha.4`. The generated `upm` branch contains the tested binaries; the Unity source directory on `main` is not itself a distributable package.

```xml
<ItemGroup>
  <ProjectReference Include="path/to/OpenGameAgent/src/OpenGameAgent/OpenGameAgent.csproj" />
  <ProjectReference Include="path/to/OpenGameAgent/src/OpenGameAgent.Memory/OpenGameAgent.Memory.csproj" />
  <!-- Optional: in-process BGE-M3 INT8 embeddings; model weights stay game-owned. -->
  <ProjectReference Include="path/to/OpenGameAgent/src/OpenGameAgent.Memory.Onnx/OpenGameAgent.Memory.Onnx.csproj" />
  <ProjectReference Include="path/to/OpenGameAgent/src/OpenGameAgent.Attachments.Local/OpenGameAgent.Attachments.Local.csproj" />
  <ProjectReference Include="path/to/OpenGameAgent/src/OpenGameAgent.Media/OpenGameAgent.Media.csproj" />
  <ProjectReference Include="path/to/OpenGameAgent/src/OpenGameAgent.Persistence/OpenGameAgent.Persistence.csproj" />
</ItemGroup>
```

Adjust the paths relative to your game project and omit optional projects you do not use. The kernel, persistence, providers, memory, attachments, plugins, and engine-compatible client are also shipped as separate `OpenGameAgent.*` release artifacts. See [Getting started](docs/getting-started.md) and [Engine integration](docs/engine-integration.md) before connecting a game.

## Why do AI NPC agents need a game-specific runtime?

General agent loops commonly assume one user, wall-clock time, and a linear conversation. A game may have multiple timelines, save forks, thousands of actors, offline time jumps, engine main-thread constraints, and actions that must never be repeated after an uncertain failure.

OpenGameAgent keeps the reusable agent machinery independent from the game while exposing the coordinates games need:

- named timelines and integer ticks, with optional calendar JSON;
- structured observations and context slices with floating-point values intact;
- content-addressed screenshot/image input with decode validation, model-capability preflight, and session-authorized retrieval;
- one message-or-tool Agent loop with no extra complexity-classifier model call;
- host-derived execution scopes that keep ordinary replies and tools available while withholding persistent planning from unauthorized actors;
- per-actor serialization with bounded cross-actor concurrency;
- journaled action intents and authoritative game receipts;
- game-time-filtered, expiring, rankable memory with session/owner-partitioned durable storage;
- optional local/remote embeddings, partitioned rebuildable vector indexes, and single-authoritative-snapshot lexical/vector hybrid recall;
- skills selected by input type and available tools;
- optional NPC self-evolution that turns structured reflection and host-verifiable evidence into immutable versioned behavior skills, composes currently visible tools into ordered procedures, and supports validation, evaluation, demotion, and exact rollback;
- optional host-published shared behavior discovery with explicit per-NPC adoption and isolated evaluation instead of automatic propagation;
- input-aware tool visibility resolved before every model request;
- host-attested tool modes and durable, one-time, world-version-bound approval for high-risk calls;
- recurring game-time triggers and persistent actor mailboxes with payload-free backlog queries;
- a typed extension API for context, tools, skills, hooks, events, providers, and services;
- capability-aware model catalogs and developer-hosted short-lived credentials;
- opt-in local discovery and health profiles for Ollama, LM Studio, LocalAI, llama.cpp, and vLLM;
- lazy external-tool discovery and large-result artifact spill;
- Agent Plugins 1.0.0 packages containing portable skills and MCP servers;
- image, audio, and video generation through replaceable APIs;
- crash-aware generated-asset materialization and authoritative engine import;
- append-only traces, provider/framework/host timing attribution, benchmark reports, observation-only playback, and offline CI evaluation;
- realtime speech with barge-in, background-agent handoff, and replaceable presentation behaviors.

The runtime does **not** decide combat legality, inventory rules, economy changes, NPC permissions, or other business rules. The game exposes narrow tools, validates every requested mutation, performs it on the correct thread or server, and returns the authoritative receipt.

## Architecture

```text
Godot / Unity / Unreal Engine sidecar / .NET game server
        |
        | GameInput (bounded JSON + GameMoment)
        v
GameAgentRuntime
  context | skills | tools | session | actor lane | extensions
        |
        v
compact stateful Agent kernel <---- steering / follow-up
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
| Agent kernel | Bounded ReAct model/tool loop, streaming typed messages, typed partial tool results, steering, follow-up, hooks, cancellation, strict transcript validation, safe zero-tool model-backed transcript compaction, provider failures as results |
| Tool execution | Provider-request schema preflight plus execution-time validation over a bounded JSON Schema subset, guaranteed result for every accepted call, ordered parallel epochs around sequential barriers, conflict-key serialization, exact-repeat loop protection, policy blocking/termination, host-attested explicit/task scopes, durable one-time approval, timeouts, uncertain write outcomes |
| Game runtime | Arbitrary JSON input, game clocks/timelines, one message-or-tool Agent loop, shared per-input usage budget, optimistic sessions, duplicate-input protection, actor concurrency, active-run steering/abort |
| Realtime conversation | Bounded PCM16 streaming, live transcription/audio events, subtitle timing, barge-in cancellation/truncation, non-blocking background-agent handoff/steering, and cancel-replace presentation behaviors |
| Image input | PNG/JPEG/WebP/GIF admission, immutable content-addressed storage, reference-only transcripts, capability preflight, tool-result images, and authorized server retrieval |
| Extension API | Immutable builder; prompt/context/tool/skill/hook/provider/service registration; per-input tool visibility; typed lifecycle events and channels; namespaced persistent state |
| Official extensions | Tool policy, high-risk execution approval and search, structured player questions/recommended replies, goals, host-verified ordered task plans with dynamic replanning and durable pause/resume, structured behavior learning and composite skills over existing tools, explicit shared behavior discovery/adoption, memory, artifacts, knowledge, restart-resumable delegated agents with lineage/leases, and tracing |
| DevTools | Bounded JSONL recordings, named context-provider and staged memory-recall timing, provider/framework/host attribution, failure and durable-write metrics, concurrent benchmark runtime, local observation-only HTML playback, and offline/CI evaluation rules |
| World primitives | Durable actions, bounded engine-thread action handoff, memories, skills, signals, game-time schedules, actor mailboxes with batch read-only pending status |
| Models and auth | Bundled capability/context/reasoning/cost directory, dynamic refresh, API-key/environment/stored/OAuth/local auth, developer-hosted short-lived credential gateway |
| External tools | Lazy on-demand search/describe/call by default; explicit direct exposure for small trusted catalogs |
| Portable plugins | [Agent Plugins 1.0.0](docs/agent-plugins.md) `plugin.json`, immediate-child `SKILL.md` discovery, MCP stdio/Streamable HTTP, client namespaces, containment, and component-level failure isolation |
| Providers | Native Anthropic, Amazon Bedrock, Google Gemini/Vertex, Mistral, OpenAI Responses/Azure, OpenAI-compatible, OpenAI Realtime, Volcengine realtime speech, remote gateway, and message-gateway transports; retry/fallback decorators; provider-neutral conformance runner and fixtures; optional local discovery for Ollama, LM Studio, LocalAI, llama.cpp, and vLLM |
| Generated media | Provider-neutral image/audio/video registry, generic async HTTP jobs, OpenRouter previews, official OpenAI Images, Volcengine Ark/Seedream, plus optional LocalAI and trusted ComfyUI workflow adapters |
| Generated assets | Stable operations, content-addressed resources, persistent lifecycle state, explicit uncertain outcomes, resumable import, and durable authoritative engine receipts |
| Persistence | Crash-tolerant local session snapshots, cross-process coordination, action journals, ordinary-tool replay journals, generated-asset jobs/resources, session/owner-partitioned memory with flat-layout migration, mailboxes, artifacts, delegations, skills, and prompt templates |
| Semantic memory | Optional model-agnostic embeddings, single-snapshot authoritative verification, partitioned rebuildable local vector indexes, lexical/vector hybrid recall, content-free stage metrics, and game-time reranking |
| Placement | Shared `netstandard2.1` runtime in Godot, Unity, or another C# host; optional .NET 8 HTTP/SSE service with C# and native C++ clients |
| Runtime protocol | Optional versioned Session/Run/Turn/Item contract, capability negotiation, stable event IDs, bounded replay/gap reconciliation, exact run/turn control, C# client, Schema/fixtures, C++ DTOs, and generated TypeScript/Python clients and reducers |
| Engines | Godot 4.7 .NET and Unity 6 in-process packages; Unreal Engine 5.8 native C++ sidecar plugin |

Realtime speech is an optional layer rather than a second game-authority path. The realtime transport can converse or transcribe, and can request reversible gaze, gesture, expression, or movement presentation. Planning and durable world mutations are handed to the same `GameAgentRuntime` and game-owned tools used by non-voice inputs. Optional OpenAI and Volcengine adapters share this contract; the Volcengine adapter keeps dialogue/VAD and streaming TTS separate from the authoritative agent loop. See [Realtime conversations](docs/realtime-conversations.md).

For an entirely local stack, the optional `OpenGameAgent.Providers.Local` package provides bounded endpoint discovery and health checks, OpenAI-compatible embeddings, composable VAD/STT/streaming-TTS speech, LocalAI image/video/TTS generation, trusted ComfyUI workflows, and explicit host-authorized model inventory/warmup/load/unload/acquisition. No model is bundled or downloaded implicitly, and unknown capabilities are not guessed. See [Local models, speech, and media](docs/local-models.md).

Run inputs, model content, tool catalogs, loops, queues, progress, and concurrency are bounded by explicit limits. Context admission runs before every model request, model and tool calls have deadlines, and large tool results can be retained as artifacts instead of repeatedly filling the prompt. Game-owned stores and rankers can replace the included in-memory or local-file implementations.

### Model access without hand-wiring every provider

`OpenGameAgent.Models.BuiltIn` turns the bundled model directory into an executable runtime. It currently dispatches nine wire APIs across 27 provider definitions and hundreds of text/tool-capable models, applying provider-specific request formats, reasoning settings, compatibility flags, cost metadata, authentication, cancellation, and bounded response handling. Provider usage is priced from the resolved directory when the provider does not report cost, while unavailable pricing remains explicitly unknown rather than appearing free. The lower provider packages remain independently usable when a game wants an explicit model and endpoint instead of a directory.

For known hosted providers, use the directory-backed runtime to construct the Agent provider. The low-level OpenAI-compatible adapter intentionally requires explicit protocol settings and does not guess a provider family from a URL or model name.

`OpenGameAgent.Models.Auth.BuiltIn` adds opt-in browser or device authorization flows for supported subscription providers. Public client registrations are never embedded in the framework: flows that require a client ID remain disabled until the game developer supplies one. Windows desktop hosts can add `OpenGameAgent.Models.Credentials.Windows` for bounded, atomic CurrentUser DPAPI persistence behind the same `IGameCredentialStore`; other platforms can provide their native secure-store implementation without changing authentication code. `OpenGameAgent.ProviderTransport` exposes only allowlisted, bounded response metadata to observers and never passes credentials or arbitrary response headers to tracing code. See [Windows credential persistence](docs/windows-credentials.md).

Image, audio, and video generation use a separate model registry because generation jobs, previews, polling, and outputs are not chat completions. The framework ships the neutral registry, generic and provider-specific adapters, and a generated-asset pipeline that materializes validated outputs before asking the authoritative game to import them. Games can register local generators, additional APIs, content-policy gates, and engine importers without changing the agent kernel. See [Generated assets](docs/generated-assets.md).

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

See the buildable [living-world example](examples/OpenGameAgent.Example/Program.cs), the offline [generated-asset example](examples/OpenGameAgent.GeneratedAssets.Example/Program.cs), and [Getting started](docs/getting-started.md).

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
- [NPC behavior learning and self-evolution](docs/behavior-learning.md)
- [Extension development kit](docs/extensions.md)
- [Provider conformance](docs/provider-conformance.md)
- [Runtime Protocol and cross-language SDKs](docs/runtime-protocol.md)
- [Engine integration](docs/engine-integration.md)
- [Deployment and security](docs/deployment-and-security.md)
- [High-risk tool approval](docs/tool-approvals.md)
- [Tool execution safety and concurrency](docs/tool-execution.md)
- [Generated media](docs/media.md)
- [Local models, speech, and media](docs/local-models.md)
- [Generated assets and authoritative import](docs/generated-assets.md)
- [Image input and game perception](docs/image-input.md)
- [Agent loop and performance](docs/agent-loop-and-performance.md)
- [Traces, playback, and offline evaluation](docs/devtools.md)

## What does OpenGameAgent provide?

This repository is a runtime framework for agents inside a game, not an AI coding agent that develops the game for you. It does not define a universal character sheet, combat model, world-package format, visual editor, or end-user game. Those belong to each game. OpenGameAgent provides the reasoning loop and game-aware primitives needed to build conversational NPCs, autonomous companions, social simulations, AI directors, generated quests and items, strategy agents, construction agents, and interactive worlds.

## Attribution

Every distributed game, mod, application, or product that includes OpenGameAgent must make the OpenGameAgent copyright and MIT license notice available in Credits, About, Third-party Licenses, documentation, or an accompanying license file. The following concise credit may be used alongside the license notice: **“Powered by OpenGameAgent | opengameagent.com”**. See the [license](LICENSE) and [brand assets and attribution guidance](docs/brand/README.md).

## License

[MIT License](LICENSE). You may build proprietary games and hosted products with the framework. Distributed copies must include the copyright and permission notice. See [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md).
