# Game Agent Runtime

An in-engine Agent Runtime for AI-native games.

Game Agent Runtime accepts typed game context, runs a streaming model/tool
loop, dispatches actions through the game engine, and records enough evidence
to recover without blindly repeating side effects. Inputs may be text, JSON,
numbers, game events, or references to game-owned resources.

> Status: `0.1.0-alpha.1`. Public APIs and the wire protocol may change before
> `1.0`.

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
- Typed observations and structured tool results; natural language is optional.
- Immutable tool and skill snapshots with bounded progressive disclosure.
- Strict tool input validation, deterministic conflict scopes, parallel reads,
  serialized conflicting writes, and engine-main-thread dispatch.
- Turn, token, duration, cost, action, queue, and provider-workload budgets.
- Request preparation, context pruning, audited derived compaction, and durable
  usage accounting without rewriting the authoritative transcript.
- Pluggable memory with local BM25, an optional bounded vector store,
  reciprocal-rank hybrid fusion, and crash-tolerant file storage.
- Exact-call and argument-churn loop guards that stop repeated tool work while
  allowing deterministic recovery after real progress.
- Cancel, interrupt, steering, and follow-up controls.
- Durable workflows for deterministic orchestration around Agent steps.
- Game-specific coordinates for named clocks, timelines, perspectives, entity
  incarnations, state versions, spatial context, and causal provenance.
- Bounded multi-actor batches and durable group interactions with isolated
  participant failures and deterministic result ordering.
- OpenAI-compatible and native Anthropic streaming provider adapters.
- A shared `netstandard2.1` core plus Godot and Unity integration boundaries.

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
| Unity 2022.3+ | In-process C# host and UPM package with managed compile, package, artifact-load, lifecycle, and conformance gates. A licensed Editor/Player gate is provided but is not claimed as executed for this alpha. |

The engine SDK is only an adapter. Agent behavior, persistence semantics, and
provider logic remain in the shared runtime.

## Start here

For a repository checkout:

```powershell
dotnet build GameAgentRuntime.sln -c Release
dotnet test GameAgentRuntime.sln -c Release --no-build
```

Then follow:

- [Getting started](docs/getting-started.md)
- [Godot integration](engines/godot/README.md)
- [Unity integration](engines/unity/README.md)
- [Tools, skills, and memory](docs/tools-skills-memory.md)
- [Game integration patterns](docs/game-integration-patterns.md)
- [Durable workflows](docs/durable-workflows.md)
- [Group interactions](docs/group-interactions.md)

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
