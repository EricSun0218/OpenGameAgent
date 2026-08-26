# Unity Asset Store listing

## Identity

- Publisher: **OpenGameAgent**
- Product: **OpenGameAgent — Agent Runtime for AI-Native Games**
- Technical package: `com.opengameagent.runtime`
- Price: **Free**
- License: **MIT** (open-source SDK/tool; review under the applicable Unity package terms)
- Category: **Tools / AI**
- Minimum editor: **Unity 6 (6000.0.0f1)**
- Tested editor: **Unity 6000.5.6f1 on Windows**
- Source: <https://github.com/EricSun0218/OpenGameAgent>
- Website: <https://opengameagent.com>

## Short description

Provider-neutral agent runtime for AI-native Unity games: structured context, typed tools, streaming, steering, local/server execution, and multi-NPC concurrency.

## Description

OpenGameAgent is an open-source runtime for building AI-native game characters and systems. Each NPC can receive structured world state, reason through an agent loop, call typed game tools, stream its response, and keep an independent life across game time.

The Unity package provides `OpenGameAgentBehaviour` plus shared runtime/client assemblies. Run agents in the Unity process for a simple local architecture, or connect to an OpenGameAgent service when credentials, server authority, or scale belong outside the player.

### Included

- Structured JSON game context and explicit game-time moments
- Stateful agent/tool loop with streamed lifecycle events
- Local in-process and remote server modes
- Actor-scoped steering, abort, cancellation, and bounded main-thread callbacks
- Multi-NPC concurrency through isolated actor lanes
- Deterministic no-key sample importable from Package Manager
- Complete MIT and third-party notices

### Designed for authoritative games

Models propose typed intents. Your game validates rules, resources, revisions, permissions, physics, pathfinding, and final state changes. Optional OpenGameAgent packages add durable actions, memory, scheduling, persistent planning, media providers, MCP, and other capabilities.

### Providers, keys, and privacy

No model, provider account, API key, or paid service is bundled. OpenGameAgent itself does not collect or transmit project data. Data leaves the game only through providers or services explicitly configured by the developer. A permanent key embedded in a player can be extracted; use BYOK, a local endpoint, developer-issued short-lived credentials, or a server.

### Getting started

Install the UPM package, import **Minimal Local Agent** from the Samples tab, add `OpenGameAgentQuickstart` to a GameObject, and enter Play Mode. Then replace the deterministic sample provider with the provider or server configuration your game uses.

## Search tags

`AI`, `agent`, `NPC`, `LLM`, `tools`, `memory`, `multi-agent`, `simulation`, `runtime`, `Unity`

## Dependencies and review disclosures

- Bundles OpenGameAgent runtime/client assemblies and .NET support assemblies listed in `Third-Party Notices.txt`.
- No external service is required for the included sample.
- Real model use requires a separately configured provider or OpenGameAgent server and may have separate terms or charges.
- The package never asks for or stores a provider key. Credential handling is chosen by the developer; permanent keys must not be shipped in player builds.
- Package code does not use project/customer data to train any model.
