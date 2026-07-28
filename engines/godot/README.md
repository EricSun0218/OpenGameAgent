# Godot 4.7.1 host

This directory is both the addon development project and its executable sample.
The distributable source is under `addons/game_agent_runtime`.

The default sample composes the durable Core runtime, injects runtime events
into the Godot event pump, streams a tool call, executes the action on the
engine main thread, journals its receipt, and completes the next model turn.

## Verify

Use the checked test runner so a Windows package-manager symlink is resolved to
the real Godot Mono executable:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File engines/godot/tests/run-godot-tests.ps1
```

The real headless SceneTree test covers:

- Autoload creation and Variant-compatible method/signal binding;
- durable observation-to-context startup and typed terminal-run resume;
- fake streaming provider Tool -> ActionReceipt -> final-output execution;
- cancel, interrupt, steer, and follow-up control behavior;
- strict dispatcher capacity, deadline, and main-thread action execution;
- injected runtime-event publication and bounded event delivery;
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
Persistence, OpenAI-compatible provider, and composition-builder assemblies.

This host currently claims Godot 4.7.1 .NET on Windows desktop/headless only.
Godot C# Web export is not supported.
