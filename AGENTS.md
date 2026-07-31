# Coding-agent guide

## Objective

This repository builds an engine-embedded Agent Runtime for games. Keep the
shared runtime independent of game engines and game-specific rules.

The repository is deliberately not a world editor, content-package format,
generic game host, or end-user application. Do not add a fixed character,
inventory, combat, event, or save schema to the shared runtime.

## Architecture boundary

- `src/GameAgent.Protocol`: strict wire DTOs and validation.
- `src/GameAgent.Core`: Agent Loop, tools, skills, memory, scheduling, budgets,
  controls, game-semantic coordinates, multi-actor coordination, and recovery.
- `src/GameAgent.Persistence`: crash-tolerant local stores.
- `src/GameAgent.Runtime`: supported composition root.
- `src/GameAgent.Workflow`: optional durable deterministic orchestration.
- `src/GameAgent.Providers.*`: provider transports only.
- `engines/godot`: primary engine adapter.
- `engines/unity`: Unity lifecycle and UPM adapter.

The game owns state, rules, permissions, legality, tools, save data, and final
mutations. A successful `ActionReceipt` must come from game code.

## First commands

From the repository root:

```powershell
dotnet build GameAgentRuntime.sln -c Release -m:1
dotnet test GameAgentRuntime.sln -c Release --no-build -m:1
```

For a fresh runnable Godot project:

```powershell
./tools/New-GameAgentGodotProject.ps1 `
  -Destination C:\work\MyAgentGame `
  -ProjectName MyAgentGame
```

The generated sample is offline and deterministic. It should compile and run
before replacing the sample provider or action.

For a machine-readable repository check:

```powershell
./tools/Invoke-GameAgentCheck.ps1 `
  -Profile fast `
  -JsonPath artifacts/check.json
```

## Change rules

1. Preserve `netstandard2.1` for shared runtime projects.
2. Keep engine SDK types out of `src`.
3. Accept arbitrary bounded structured context; do not assume user input is
   natural language.
4. Put business adjudication in game handlers, not in the runtime.
5. Bound every externally controlled collection, string, payload, queue,
   callback, timeout, and concurrency path.
6. Never infer that cancellation means a side effect did not commit.
7. Keep durable input, operation, receipt, and recovery identities stable.
8. Do not install executable code from a skill or other untrusted content at
   runtime.
9. Add focused tests for validation limits, failure paths, cancellation,
   concurrency, and deterministic ordering.
10. Do not add credentials, local machine paths, private research notes, or
    generated release artifacts to source control.

## Verification by change area

- Core/runtime/provider change: solution build and all solution tests.
- Persistence change: add restart/corruption/atomicity tests.
- Godot change: `engines/godot/tests/run-godot-tests.ps1`.
- Unity change: `engines/unity/scripts/Test-UnityPackage.ps1`.
- Packaging change: build the affected artifact, run its artifact verifier,
  and run tracked-source privacy checks.

Read `docs/coding-agent-quickstart.md` before implementing a game integration.
