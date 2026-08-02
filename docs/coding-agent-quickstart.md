# Coding-agent quickstart

This guide is written for a coding agent integrating Game Agent Runtime into a
game. The target outcome is one verified Agent Loop that receives structured
game state, invokes one authoritative game action, persists its receipt, and
returns a final result.

Before mapping a larger character or simulation system, read
[game integration patterns](game-integration-patterns.md).
For persistent characters, delayed messages, group scenes, world events, and
high-level tactics, also read
[living-world integration](living-world-integration.md).

## 1. Establish the boundary

Before editing code, identify:

- the observation producer: which bounded game state is exposed;
- the action handler: which game-owned method validates and mutates state;
- the authority thread: whether the engine main thread is required;
- the session identity and save/state revision;
- the provider credential source;
- the durable data directory;
- per-run and global budgets.

Do not move the game's business model into the runtime. Model a game capability
as a typed tool and keep legality in its handler.

## 2. Prove the repository

```powershell
dotnet build GameAgentRuntime.sln -c Release -m:1
dotnet test GameAgentRuntime.sln -c Release --no-build -m:1
```

If either command fails before your change, report the failure separately from
the integration work.

## 3. Start from an executable path

Godot is the primary path:

```powershell
./tools/New-GameAgentGodotProject.ps1 `
  -Destination C:\work\MyAgentGame `
  -ProjectName MyAgentGame
```

The script builds the release addon, creates a new project atomically, and
installs an offline deterministic tool-loop sample. It refuses to overwrite an
existing destination.

Run the generated project once before changing it. The sample proves:

- the addon and shared assemblies load;
- provider work runs away from the engine main thread;
- a structured observation reaches the model request;
- a tool call reaches a main-thread game handler;
- an authoritative receipt is journaled;
- the next turn returns final structured output;
- bounded shutdown flushes the durable store.

## 4. Replace one seam at a time

Recommended order:

1. Replace the sample observation with a projection of real game state.
2. Replace `gather_food` with one real, narrow action handler.
3. Keep the deterministic provider and verify the action path.
4. Add a real provider and credential source.
5. Move the journal from the sample temporary store to the game's persistent
   data directory.
6. Add memory only after the basic run and recovery path pass.
7. Add multi-actor batches only after one actor is correct.

This order makes provider variability the last new variable rather than the
first.

## 5. Context is typed data

Use `ObservationEnvelope.Payload` for bounded JSON and resource references for
large game-owned data. Include only facts visible to the acting perspective.
Attach state version, game time, timeline, and entity incarnation when stale or
cross-save data could be dangerous.

Natural language is optional. A compact object such as this is valid input:

```json
{
  "self": { "hp": 18, "position": [4, 9] },
  "visibleTargets": ["enemy-7"],
  "legalActionIds": [2, 5, 8],
  "tick": 4210
}
```

## 6. Tool contract

For every tool:

- use a closed JSON Schema;
- declare read, game-write, or external-write effect;
- declare the narrowest deterministic conflict scopes;
- require engine-main-thread affinity only when necessary;
- set a finite timeout;
- define retry and idempotency deliberately;
- revalidate permissions and state inside the handler;
- return the actual `ActionReceipt` outcome.

Never convert an `unknown` outcome to success and never retry it without game
reconciliation.

## 7. Production gate

Run the machine-readable fast gate during iteration:

```powershell
./tools/Invoke-GameAgentCheck.ps1 `
  -Profile fast `
  -JsonPath artifacts/check-fast.json
```

Before release, run the applicable engine profile and inspect the JSON report.
The report schema is stable enough for a coding agent to locate the first
failed check without parsing localized console output.

## Completion criteria

An integration is not complete until:

- a structured observation drives a real tool decision;
- the handler runs on the correct thread;
- a receipt is durable before the next dependent mutation;
- restart behavior is tested for both known and unknown write outcomes;
- budgets and queue bounds are explicit;
- provider secrets are absent from the client or are player-owned;
- shutdown drains or reports an explicit incomplete state.
