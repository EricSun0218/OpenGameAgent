# OpenGameAgent

[简体中文](README.zh-CN.md)

An open-source C# Agent Runtime for AI-native games, autonomous NPCs, and
living-world simulations. Embed it in Godot or Unity, or host the same runtime
in a .NET game service.

[![CI](https://github.com/EricSun0218/OpenGameAgent/actions/workflows/ci.yml/badge.svg)](https://github.com/EricSun0218/OpenGameAgent/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![Status](https://img.shields.io/badge/status-alpha-orange.svg)](CHANGELOG.md)

OpenGameAgent accepts typed game context, runs a streaming model/tool
loop, dispatches actions through the game engine, and records enough evidence
to recover without blindly repeating side effects. Inputs may be text, JSON,
numbers, game events, or references to game-owned resources.

> Status: `0.2.0-alpha.1`. Public APIs and the wire protocol may change before
> `1.0`.

## Why a game-specific Agent Runtime?

General-purpose Agent loops assume one user, wall-clock time, and a mostly
linear conversation. Games need named clocks and timelines, save forks,
authoritative state changes, bounded frame-thread work, many concurrent NPCs,
deterministic conflict handling, offline simulation, and recovery that never
blindly repeats a side effect. OpenGameAgent makes those concerns part of
the reusable runtime while leaving game rules and state ownership in game code.

Use it to build conversational characters, autonomous companions, AI game
masters, social deduction agents, generated quests and content, persistent
living worlds, or traditional mechanics controlled through typed tools.

## Product boundary

This repository is an Agent Runtime, not a game, world editor, content format,
or end-user host. The game owns its state, rules, permissions, UI, save format,
and final mutations. The runtime owns the reusable Agent Loop and its safety,
durability, scheduling, memory, and provider boundaries.

That split lets the same runtime drive an NPC, a director, a simulation worker,
an assistant, or a group decision without forcing games into one data model.

## Capabilities

- Durable streaming model/tool loops with retries, route fallback, stale-stream
  fencing, crash recovery, and explicit reconciliation of uncertain writes.
- Stateless completion plus durable `Direct`, full `Agent`, and fixed
  `Workflow` paths with bounded hybrid routing: obvious dialogue stays fast,
  actionable or structured input retains Agent capabilities, and declared
  requirements always win.
- Typed observations and structured tool results; natural language is optional.
- Immutable tool and skill snapshots with bounded progressive disclosure.
- Strict tool input validation, deterministic conflict scopes, parallel reads,
  serialized conflicting writes, and engine-main-thread dispatch.
- Turn, token, duration, cost, action, queue, and provider-workload budgets.
- Request preparation, context pruning, audited derived compaction, and durable
  usage accounting without rewriting the authoritative transcript, with a
  replaceable bounded conversation-context engine.
- Pluggable memory with local BM25, an optional bounded vector store,
  reciprocal-rank hybrid fusion, bounded query transforms and reranking, and
  crash-tolerant file storage.
- Exact-call and argument-churn loop guards that stop repeated tool work while
  allowing deterministic recovery after real progress.
- Cancel, interrupt, steering, and follow-up controls.
- Per-operation reasoning, sampling, prompt-cache, and ordered provider-route
  controls that are mapped explicitly or rejected before transport.
- Typed required/optional lifecycle middleware around runs, model dispatch, and
  tool batches, with bounded callback isolation.
- Durable workflows for deterministic orchestration around Agent steps.
- Safe model-authored command plans with ordered and parallel DAG stages,
  bounded foreach/reduce/feedback loops, durable waits, and host receipts.
- Game-specific coordinates for named clocks, timelines, perspectives, entity
  incarnations, state versions, spatial context, and causal provenance.
- Durable game-time triggers, persistent Agent identities and mailboxes,
  bounded residency, session-bound context deltas, cited memory distillation,
  external attention, and hierarchical world/group/Agent budgets.
- Bounded multi-actor batches and durable group interactions with isolated
  participant failures and deterministic result ordering.
- Bounded child Agent supervision with durable lineage, depth and concurrency
  limits, cancellation propagation, and failure-isolated batches.
- Native OpenAI Responses, Gemini Interactions, Anthropic Messages, and
  configurable OpenAI-compatible streaming provider adapters, with immutable
  configured-model catalogs and explicit capability negotiation.
- Provider-neutral image, video, speech, and structured-content jobs for local
  or remote APIs, with durable polling/cancellation, safe artifact import,
  streaming speech, and host-validated content transactions; no model is
  bundled.
- A shared `netstandard2.1` core plus Godot and Unity integration boundaries.
- Optional engine/server placement with an authenticated WebSocket action
  bridge, SQLite/PostgreSQL journals, tenant admission, and standard telemetry.
- Deterministic living-world level-of-detail scheduling and offline gameplay
  evaluation for large NPC populations.

## Architecture

```text
game code
  observations -> Agent Runtime -> action requests
       ^                              |
       |                         game handlers
       +------- authoritative receipts

Agent Runtime
  context + memory + skills + tools
  -> provider stream
  -> validated/scheduled tool calls
  -> journals + checkpoints + metrics
```

An `ActionReceipt` is the authority boundary. Only the game can report that a
mutation succeeded, was rejected, failed, or has an unknown outcome. The
runtime never invents a successful game-state change.

Read [architecture](docs/architecture.md), [protocol](docs/protocol.md), and
[game semantics](docs/game-semantics.md) for the detailed contracts.

## Engine support

| Target | Current scope |
| --- | --- |
| Godot 4.7 .NET | Primary integration. In-process C# runtime, Autoload lifecycle, typed and GDScript bridges, bounded main-thread/event pumps, multi-actor support, packaging, and Windows desktop/headless verification. |
| Unity 2022.3+ | In-process C# host and UPM package with managed compile, package, artifact-load, lifecycle, and conformance gates. Unity 6000.5.6f1 on Windows has passed the licensed EditMode, PlayMode, Mono Player, and IL2CPP Player build-and-run gates. |

The engine SDK is only an adapter. Agent behavior, persistence semantics, and
provider logic remain in the shared runtime.

Reusable Agent behavior can run in-process with the game or in a .NET game
service. A remote model endpoint alone changes only model transport. A hosted
runtime may own workflows, memory, and its journal, while game rules, saves,
and authoritative action settlement remain game-owned.

## Start here

For a repository checkout on Windows or Linux:

```powershell
dotnet build GameAgentRuntime.sln -c Release
dotnet test GameAgentRuntime.sln -c Release --no-build
```

Then follow:

- [Getting started](docs/getting-started.md)
- [Godot integration](engines/godot/README.md)
- [Unity integration](engines/unity/README.md)
- [Tools, skills, and memory](docs/tools-skills-memory.md)
- [Execution and extension reference](docs/execution-and-extension-reference.md)
- [Route work and supervise child Agents](docs/how-to-route-and-supervise-agents.md)
- [Game integration patterns](docs/game-integration-patterns.md)
- [Living-world integration](docs/living-world-integration.md)
- [Living-world scheduling](docs/living-world-scheduling.md)
- [Deployment and remote hosting](docs/deployment-and-remote-hosting.md)
- [Evaluation and observability](docs/evaluation-and-observability.md)
- [Native model providers](docs/native-model-providers.md)
- [Runtime capability model](docs/runtime-capability-model.md)
- [Media and generated content](docs/media-and-generated-content.md)
- [Durable workflows](docs/durable-workflows.md)
- [Group interactions](docs/group-interactions.md)

Questions and support requests belong in [GitHub Discussions](https://github.com/EricSun0218/OpenGameAgent/discussions)
after the public launch. Bugs and feature proposals use the repository issue
forms. Suspected vulnerabilities must follow [the security policy](SECURITY.md).

## Security and deployment

Run the Agent Runtime in the game process when low latency and direct engine
integration matter. Do not ship a provider secret that grants access to your
commercial account inside a client build. Use player-supplied credentials for
BYOK deployments or exchange game authentication for short-lived, scoped
access through a service you control.

Tools and skills are capabilities, not prompt text. Keep authoritative
validation and mutations in game code, expose the narrowest tool surface, and
persist operation receipts before assuming a write can be retried.

## Release verification

The repository includes deterministic package, privacy, version consistency,
managed consumer, Godot, Unity, performance, and live-provider gates. See
[pre-public release](docs/pre-public-release.md) before publishing an artifact.

## License

OpenGameAgent is licensed under the [Apache License 2.0](LICENSE). Read the
[contribution guide](CONTRIBUTING.md) and [code of conduct](CODE_OF_CONDUCT.md)
before contributing.
