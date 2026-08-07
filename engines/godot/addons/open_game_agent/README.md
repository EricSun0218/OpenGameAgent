# OpenGameAgent for Godot

Godot 4.7 .NET adapter for the OpenGameAgent runtime.

1. Copy this `open_game_agent` directory into the project's `addons` directory.
2. Import `addons\open_game_agent\OpenGameAgent.Godot.props` from the game's C# project.
3. Add `OpenGameAgentNode` to a scene or Autoload.
4. Call `Configure(runtime)` for in-process execution or `ConfigureRemote(client)` for a .NET service.

Use the typed async methods from C#. `SteerActorAsync` and `AbortActorAsync` work in local and remote modes. For GDScript, call `RunJson` and connect to `run_event`, `run_completed`, and `run_failed`. Stream signals use a bounded queue drained on the Godot thread; optional `Configure` arguments tune active runs, queue capacity, and per-frame budget. Admitted runs reserve terminal delivery.

The distributable add-on contains its shared runtime DLLs. Generated packages belong in `engines/godot/artifacts` and are not committed.

Full setup, lifecycle, cancellation, and main-thread guidance is in `docs/engine-integration.md` at the repository root.
