# Godot Asset Store listing

## Identity

- Publisher: **OpenGameAgent**
- Asset name: **OpenGameAgent**
- Slug: `opengameagent`
- Price: **Free**
- License: **MIT**
- Category: **Tools / AI**
- Minimum engine: **Godot 4.7 .NET**
- Source: <https://github.com/EricSun0218/OpenGameAgent>
- Website: <https://opengameagent.com>

## Summary

Provider-neutral agent runtime for AI-native Godot games. Give NPCs structured world context, tool use, streaming responses, steering, and independent concurrent lives.

## Description

OpenGameAgent is an open-source runtime for building AI-native game characters and systems. It turns structured game state into an agent loop that can reason, call typed game tools, stream results, and continue across game time—without moving game authority into the model.

The Godot 4.7 .NET add-on provides an `OpenGameAgentNode` that runs either in process or against an OpenGameAgent server. It exposes typed C# methods plus bounded, main-thread Godot signals for GDScript and UI integration.

### Included

- Structured `GameInput` with arbitrary JSON context and game-time moments
- Stateful agent/tool loop with streaming lifecycle events
- Local in-process and remote server modes
- Actor-scoped steering, abort, cancellation, and bounded callback delivery
- Multi-NPC concurrency through isolated actor lanes
- Shared runtime/client assemblies
- Deterministic no-key example
- MIT license and source code

### Game authority

Models propose typed intents. Your game still validates rules, resources, revisions, permissions, physics, pathfinding, and final state changes. Optional OpenGameAgent packages add durable actions, memory, scheduling, persistent planning, media providers, MCP, and other capabilities.

### Providers and privacy

No model or paid service is bundled. OpenGameAgent itself does not collect or transmit project data. Data leaves the game only through providers or services explicitly configured by the developer. Do not embed a permanent provider key in an exported client; use BYOK, a local endpoint, short-lived credentials, or a server.

### Installation

Copy `addons/open_game_agent` into a Godot 4.7 .NET project, import `OpenGameAgent.Godot.props` from the project `.csproj`, and add `OpenGameAgentNode` to a scene or Autoload. Full instructions are included and maintained online.

## Search tags

`AI`, `agent`, `NPC`, `LLM`, `tools`, `memory`, `multi-agent`, `simulation`, `runtime`, `Godot C#`

## Review notes

- The add-on is a Godot 4.7 .NET runtime library; the GDScript editor plug-in is intentionally side-effect free.
- The bundled sample is deterministic and makes no network request.
- External provider packages, accounts, API terms, costs, and data policies are not included.
- Package path is `addons/open_game_agent` and the license is present inside that directory.
