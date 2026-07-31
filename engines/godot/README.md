# Godot 4.7.1 host

This directory is the primary engine integration, its development project, and
an executable sample. Distributable addon sources live under
`addons/game_agent_runtime`.

The addon hosts the shared durable Agent Runtime in the Godot process. It owns
scene lifecycle, bounded main-thread action dispatch, bounded signal delivery,
Variant mapping, cancellation, and shutdown. The game still owns rules, state,
tools, credentials, and save data.

## Verify

Use the checked runner so Windows package-manager links are resolved to the
real Godot Mono executable:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File engines/godot/tests/run-godot-tests.ps1
```

If the editor reports ".NET assemblies not found", launch the real executable
through the repository helper:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File engines/godot/tools/Launch-GodotEditor.ps1
```

To install the pinned editor into an explicit location:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File engines/godot/tools/Install-PinnedGodot.ps1 `
  -InstallRoot E:\GameAgentTools\Godot
```

## Build the distributable addon

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File engines/godot/tools/package-addon.ps1
```

The package contains the Godot adapter and release-built shared assemblies. It
does not contain credentials, a game data model, or executable user content.
Run the artifact verifier after packaging:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File engines/godot/tests/verify-packaged-addon.ps1
```

## Runtime path

1. Enable the **Game Agent Runtime** plugin.
2. The plugin registers `GameAgentRuntimeNode.tscn` as the
   `GameAgentRuntime` Autoload.
3. Compose a `BuiltGameAgentRuntime` with `GameAgentRuntimeBuilder`.
4. Call `runtimeNode.Typed.ConfigureDurable(builtRuntime)` before starting
   runs.
5. Register game action handlers on `GodotMainThreadGameHost` and return
   authoritative `ActionReceipt` values.
6. Consume typed tasks from C#, or runtime signals and Variant dictionaries
   from GDScript.

See the [addon README](addons/game_agent_runtime/README.md) and
[getting started](../../docs/getting-started.md).

## Verified scope

The Windows desktop/headless path is verified against Godot 4.7.1 .NET. Linux
packaging inputs are pinned, but Linux is not required for the runtime to run
inside Godot and is not a substitute for a platform-specific release gate.
