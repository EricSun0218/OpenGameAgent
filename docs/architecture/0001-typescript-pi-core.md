# ADR 0001: TypeScript runtime with a direct Pi Agent core

Status: accepted

## Decision

OpenGameAgent is implemented as TypeScript packages on Node.js. The production message-or-tool loop directly uses
`@earendil-works/pi-agent-core` and `@earendil-works/pi-ai`.

OpenGameAgent does not implement another model loop, route classifier, workflow runtime, provider stream parser, or Tool
executor. Its core responsibilities are game input projection, host-owned Tool authority, canonical runtime events, exact
run and turn control, session composition, and durable game-specific boundaries.

The initial package graph is deliberately small:

```text
@opengameagent/protocol
          ↓
@opengameagent/agent
          ↓
@opengameagent/runtime
```

Packages split later only for an optional native dependency, a deployment boundary, a stable third-party implementation
point, an independent release surface, or proven cycle avoidance.

The Unity 6, Godot, and Unreal integrations consume the same protocol through thin clients and host adapters. Their C#
and C++ implementation substrates are scoped to those engines and do not form additional SDK products or contain a
second Agent runtime.

## Consequences

- Pi Agent events and state are adapted rather than copied.
- Provider-specific hidden reasoning and opaque continuation data are not projected into canonical public events.
- Game rules and authoritative writes stay behind registered host Tools.
- The local sidecar and server deployment modes share the same runtime packages.
