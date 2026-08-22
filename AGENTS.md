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
- `src/OpenGameAgent.Media`: media model routing plus persistent generated-asset lifecycle contracts.
- `src/OpenGameAgent.Client`: engine-compatible JSON/SSE service client.
- `src/OpenGameAgent.Runtime.Protocol`: optional versioned, transport-neutral Session/Run/Turn/Item contract.
- `src/OpenGameAgent.Runtime.Hosting`: optional in-process Runtime projection and bounded replay journal.
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

### Requirement intake and closure

- Every user request and cross-thread delegation must be added to the active task plan before implementation starts.
- First check whether the requested capability already exists in source, tests, documentation, or a published artifact. Do not implement a duplicate subsystem from a request alone.
- Before designing or implementing a confirmed gap, classify it as `open-source`, `closed-source`, or `mixed` and record the rationale in the active plan.
  - `open-source`: reusable, replaceable, self-hostable runtime contracts, SDKs, protocol schemas, security primitives, extension points, and conformance tests needed by arbitrary game hosts.
  - `closed-source`: hosted control planes, organization or tenant administration, managed identity and secrets, quotas, billing, private registries, managed observability or storage, commercial scheduling or high availability, SLAs, and proprietary hosted optimization.
  - `mixed`: split the requirement. Keep only the smallest provider-neutral, independently self-hostable contract and conformance surface in this repository; route the managed implementation to the separate closed-source product.
- A closed-source requirement, or the closed-source portion of a mixed requirement, must not be implemented in this repository. Forward the exact context, ownership boundary, and acceptance criteria to the user-designated closed-framework task, track the handoff in the active plan, and notify the user. Re-evaluate the classification if later evidence changes the boundary.
- Keep `ROADMAP.md` synchronized with every accepted framework requirement. Record its source-independent capability, priority, acceptance evidence, and one of: planned, in progress, completed, superseded, paused, or rejected.
- A requirement is completed only when its implementation, focused tests, full applicable release gates, documentation, commit, push, and source-thread notification are all complete. Green CI alone is not proof that the requirement backlog is empty.
- When a newer requirement supersedes an older one, retain the decision in `ROADMAP.md` instead of silently dropping the older item.
- Do not stop with accepted planned or in-progress items. Paused items require an explicit user boundary; rejected or superseded items require a written rationale.

- Kernel changes require success, validation, provider-failure, cancellation, limit, and event-order tests where relevant.
- State-changing tool changes require duplicate, uncertain-outcome, and recovery tests.
- Persistence changes require restart, corruption, atomic-write, and concurrency coverage proportional to the change.
- Generated-asset changes require duplicate submission, uncertain generation/import, restart, resource-integrity, and authoritative receipt coverage.
- Routing changes require explicit, typed, classifier-failure, and conservative-fallback tests.
- Actor or tool concurrency changes require deterministic ordering and saturation tests.
- Wire/client/server changes require JSON and SSE integration tests.
- Engine changes require package compilation and the relevant real-editor smoke test before release.
- Product, version, or compatibility changes must update both root READMEs.

Prefer a small composable interface over a new subsystem. Add game-specific behavior as an example or adapter unless multiple unrelated game designs require the same semantics.
