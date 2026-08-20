# Coding agent guide

## Objective

OpenGameAgent is a small C# agent runtime for games. Preserve a compact, understandable core while supporting structured game input, safe actions, game time, multiple actors, and engine/server placement.

This repository is a framework, not a game, world editor, content marketplace, or universal entity schema.

## Repository map

- `src/OpenGameAgent.Kernel`: engine-neutral stateful model/tool loop.
- `src/OpenGameAgent`: game coordinates, runtime composition, actions, routing, sessions, workflows, memory, skills, scheduling, mailboxes, and media contracts.
- `src/OpenGameAgent.Persistence`: crash-tolerant local-file implementations.
- `src/OpenGameAgent.DevTools`: bounded trace recording, observation-only playback, and offline evaluation.
- `src/OpenGameAgent.Providers.OpenAICompatible`: streaming chat-completions transport.
- `src/OpenGameAgent.Providers.MediaHttp`: generic remote or local media-generation transport.
- `src/OpenGameAgent.Providers.OpenAI.Images`: official OpenAI image generation/edit transport.
- `src/OpenGameAgent.Providers.Volcengine.Images`: Volcengine Ark/Seedream image generation/edit transport.
- `src/OpenGameAgent.Client`: engine-compatible JSON/SSE service client.
- `src/OpenGameAgent.Server`: optional C# .NET 8 host.
- `engines/godot`: Godot .NET package and tests.
- `engines/unity`: Unity UPM package and tests.
- `engines/unreal`: native Unreal Engine C++ plugin for the remote JSON/SSE placement.
- `examples`: buildable integration examples.
- `tools/OpenGameAgent.DevTools.Cli`: local trace inspection and CI evaluation CLI.
- `tests`: focused unit and integration tests.

## Non-negotiable boundaries

1. Shared projects remain `netstandard2.1`; the server and tests use .NET 8.
2. Engine SDK types never enter `src`.
3. Input and context are bounded JSON and must not assume natural language.
4. Keep floating-point JSON values; do not coerce game data to strings or integers.
5. Game code owns rules, permissions, authoritative state, and final mutations.
6. Model output is untrusted. Validate schemas and revalidate in game handlers.
7. Never infer that cancellation or timeout means a write did not commit.
8. Side effects need stable operation IDs and an explicit recovery answer.
9. Same-actor ordering and bounded cross-actor concurrency must remain deterministic.
10. Narrative scheduling and memory follow game time. Operational leases may use real time.
11. Skills are instructions and metadata, not executable code loaders.
12. Bound every externally controlled payload, collection, queue, callback, loop, timeout, and concurrency path.
13. Do not add provider credentials, private research, proprietary game data, local absolute paths, or generated engine state.
14. Keep public source and comments self-contained; explain decisions in terms of OpenGameAgent behavior.

## First commands

```powershell
dotnet restore OpenGameAgent.sln
dotnet build OpenGameAgent.sln -c Release --no-restore
dotnet test OpenGameAgent.sln -c Release --no-build --no-restore
dotnet format OpenGameAgent.sln --verify-no-changes --no-restore
```

Package gates:

```powershell
./engines/godot/test-package.ps1 -GodotSharpDir <GodotSharp/Api/Debug>
./engines/unity/test-package.ps1 -UnityManagedDir <Unity/Editor/Data/Managed/UnityEngine>
./engines/unreal/test-package.ps1
```

Real-editor gates are in `docs/engine-integration.md`.

## Change guidance

- Kernel changes require success, validation, provider-failure, cancellation, limit, and event-order tests where relevant.
- State-changing tool changes require duplicate, uncertain-outcome, and recovery tests.
- Persistence changes require restart, corruption, atomic-write, and concurrency coverage proportional to the change.
- Routing changes require explicit, typed, classifier-failure, and conservative-fallback tests.
- Actor or tool concurrency changes require deterministic ordering and saturation tests.
- Wire/client/server changes require JSON and SSE integration tests.
- Engine changes require package compilation and the relevant real-editor smoke test before release.
- Product, version, or compatibility changes must update both root READMEs.

Prefer a small composable interface over a new subsystem. Add game-specific behavior as an example or adapter unless multiple unrelated game designs require the same semantics.
