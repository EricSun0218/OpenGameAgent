# Godot 4.7.1 host

This directory is both the addon development project and its executable sample.
The distributable source is under `addons/game_agent_runtime`.

The default sample composes the durable Core runtime, injects runtime events
into the Godot event pump, streams a tool call, executes the action on the
engine main thread, journals its receipt, and completes the next model turn.

The Autoload also contains an `InteractiveWorld` child. `ConfigureNative`
activates the built-in declarative package evaluator and gives the engine one
atomic package/save/runtime generation. A lower-level `Configure` path accepts
an engine-neutral `InteractiveWorldFacade` with game-owned handlers. Neither
path defines game rules or numeric fields for the developer.

Enabling the editor plugin adds an **Agent World** dock for creating a starter
world, validating its closed JSON contracts, and building a deterministic
`.gaworld` archive. The distributable addon carries the world-v1 JSON Schemas
and an interactive example under `authoring/`. The same dock can validate
optional Character Card JSON/PNG and lorebook JSON, then build an explicitly
accepted untrusted-data binding for one agent. The archive reader revalidates
the strict inert-content contracts after reload before character/lore
activation; bindings never grant tools, skills, credentials, or code.

## Verify

Use the checked test runner so a Windows package-manager symlink is resolved to
the real Godot Mono executable:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File engines/godot/tests/run-godot-tests.ps1
```

The same distinction matters when opening the editor. Some Windows package
managers expose Godot through a filesystem link; launching that link can make
the .NET build look for `GodotSharp/Api/Debug` beside the link instead of
beside the real Mono executable. If the editor reports ".NET assemblies not
found", launch it with the repository helper:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File engines/godot/tools/Launch-GodotEditor.ps1
```

The helper resolves the link before starting the visible editor. It does not
modify the package-manager installation or the system link.

The real headless SceneTree test covers:

- Autoload creation and Variant-compatible method/signal binding;
- durable observation-to-context startup, advanced GDScript start/resume
  options, semantic resume guards, fail-closed option bounds, and typed
  terminal-run resume;
- bounded concurrent multi-NPC batches, fail-fast aggregate hard-budget
  reservation, input-ordered results, durable manifests, guarded
  resume/abandonment, shared world/timeline/save/session coordinates, pending
  reconciliation, and batch/actor lifecycle signals;
- fake streaming provider Tool -> ActionReceipt -> final-output execution;
- cancel, interrupt, steer, and follow-up control behavior;
- strict dispatcher capacity, deadline, and main-thread action execution;
- injected runtime-event publication and bounded event delivery;
- event-pump flood handling with dropped-event and dispatch-latency metrics;
- native world package/save byte and file round trips, typed structured
  interaction parity, stale state/catalog rejection, and main-thread world
  result delivery;
- one complete framework scene covering character-card and world-book import,
  fail-closed untrusted-data activation, explicit agent-profile selection,
  editor starter/validation/package build, native engine-session activation,
  two concurrent durable NPC selections with isolated private structured
  context and strict final-output admission, selection-driven structured
  interaction, authoritative receipt-gated memory/group/presentation
  settlement, deterministic bundle import, fixed-point numeric effects,
  monthly game-time evolution, atomic native save/reload, continued
  evolution, controlled shutdown, and portable digest parity;
- legacy `ConfigureHeadless` and `start_run` compatibility;
- runtime dispose -> flush -> store dispose shutdown ordering, including a
  flush-failure path.

## Package and isolated verification

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File engines/godot/tools/package-addon.ps1 `
  -Configuration Release -Version 0.1.0-test

powershell -NoProfile -ExecutionPolicy Bypass `
  -File engines/godot/tests/verify-packaged-addon.ps1
```

The verifier extracts the archive into a separate consumer project, compiles
the addon through Godot, and launches its Autoload in a real headless
SceneTree. The archive includes the netstandard2.1 Protocol, Core,
Persistence, OpenAI-compatible and native Anthropic providers,
composition-builder, optional durable Workflow, Compatibility, and World
assemblies, plus the authoring schemas and example.

This host currently claims Godot 4.7.1 .NET on Windows desktop/headless only.
Godot C# Web export is not supported.

## Entity-incarnation observation fence

Godot observation dictionaries preserve protocol `extensions`. When strict
incarnation admission is enabled in `DurableAgentRuntimeOptions`, every
audience-restricted observation passed to `start_agent_run`, `steer_run`, or
`follow_up_run` must include a complete `audienceIncarnations` extension:

```json
{
  "extensions": {
    "audienceIncarnations": [
      {
        "audienceId": "agent-17",
        "entityId": "npc-42",
        "incarnation": 3
      }
    ]
  }
}
```

The binding is retained when the Variant mapper converts the observation into
durable context. It must match the run's `gameContext.observer`; a stale
binding is rejected before the provider step can be interrupted or started.
