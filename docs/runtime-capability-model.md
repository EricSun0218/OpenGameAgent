# Runtime capability model

This document separates reusable runtime behavior from game-specific behavior.
The runtime does not contain a building system, combat system, dialogue system,
or simulation rules. It supplies the execution contracts that let those systems
be driven by an Agent without treating model text as authoritative game state.

## The authoritative action loop

For every game mutation, the reusable path is:

```text
typed observation or event
  -> model response
  -> admitted tool call
  -> schema and conflict validation
  -> durable operation intent
  -> game-owned handler
  -> authoritative ActionReceipt
  -> next observation, completion, or reconciliation
```

The game registers narrow business operations such as `inspect_region`,
`place_object`, `move_actor`, `apply_damage`, or `advance_economy`. The runtime
does not assume what those operations mean. A successful-looking model message
cannot change the world; only a game handler and its `ActionReceipt` can do so.

This same contract supports a short conversation, a long construction job, an
NPC turn, a director, or a simulation worker. A long job may inspect state,
publish a plan, execute several host operations, observe a rejection, revise the
plan, and continue. If a process stops after dispatch but before a receipt is
durable, recovery pauses for reconciliation instead of blindly repeating the
write.

For one long host operation, implement `IProgressReportingGameHost` and report
bounded `GameActionProgress` through the supplied execution-scoped sink. This
can drive construction steps, pathing phases, downloads, crafting batches, or
other game-owned work without inventing a framework-specific building system.
Progress is live presentation state; only the final `ActionReceipt` can settle
the operation.

Inputs are typed JSON values and resource references. Natural language is one
possible value, not the runtime protocol.

## Choosing the least expensive execution path

The runtime exposes four execution shapes:

| Shape | Durable | Model turns | Game tools | Typical use |
| --- | --- | --- | --- | --- |
| Completion | No | One | No | classification, extraction, rewrite |
| Direct | Yes | One | Hidden | durable dialogue or one-shot decision |
| Agent | Yes | Bounded loop | Yes | stateful NPC or world action |
| Workflow | Yes | Declared | Per stage | deterministic orchestration |

The deterministic router selects the least-capable shape that satisfies the
request. A simple line of dialogue therefore does not need to enter a complex
tool loop. Games can supply a bounded custom routing policy; invalid or timed-out
decisions fall back to the least-capable valid route.

## Game-native runtime primitives

### Game time and triggers

`GameTimePoint` carries a named clock, timeline, epoch, and tick. It does not
assume wall time. `GameTriggerCoordinator` converts game-provided occurrences
such as a turn, day, month, season, encounter, or quest transition into durable
launches. Catch-up policies are `all`, `once`, `skip`, and `coalesce`; overlap
policies are `queue`, `skip`, `coalesce`, and `replace`.

Coalescing may merge into work that is still queued. It never rewrites the
input of a launch that is already running; later occurrences become a bounded
queued successor so the host cannot silently miss them.

The game remains the clock authority. The runtime never advances a calendar or
invents an occurrence.

### Persistent Agents and bounded residency

`PersistentAgentGraph` stores stable Agent identities, lineage/group edges,
history identity, lifecycle state, and bounded mailboxes. A world may keep many
logical NPCs while `AgentResidencyManager` loads only a bounded working set.
Eviction is deterministic and refuses to unload busy Agents or Agents that own
an unsettled side effect. Execution capacity and model-call capacity are
separate so resident NPC state does not imply an unbounded number of requests.

The bundled aggregate file store is a bounded local baseline. Large production
worlds can implement the same store interfaces with sharding or a database;
the runtime does not claim that one append-only aggregate file is a high-write
100,000-NPC database.

### Multi-actor decisions

Multi-actor batches capture immutable participant identity and game-state
coordinates, reserve aggregate budgets before work starts, isolate participant
failures, and return deterministic ordering. Group interactions additionally
separate private and shared memory and support durable settlement. The game
chooses simultaneous-action conflict and settlement rules.

### Context and memory

Model-visible context sections support full snapshots, merge-patch deltas, and
unchanged markers. Delta baselines are bound to the exact session or run, so a
view shown to one conversation cannot become an unexplained delta in another.

Memory providers are replaceable. The repository includes bounded lexical and
optional vector search, hybrid fusion, query transforms, game-aware reranking,
file persistence, and cited memory distillation. Distilled records keep source
citations and provenance, use explicit confidence and salience, and may retain
or retire against game time instead of wall time.

### Wait, signal, cancellation, and control

The Agent loop supports cancel, interrupt, steering, and follow-up controls.
`ExternalAttentionCoordinator` adds durable request/resolution records for work
that must pause for a player choice, game event, approval, or another subsystem.
Requests and resolutions are idempotent and bound to an authority and state
digest.

### Hierarchical budgets

Durable charges can roll up through an Agent, group, world, save, account, or
other host-defined hierarchy. Limits cover model calls, cached and uncached
tokens, output tokens, tool actions, host side effects, resident time, model
cost, and media cost. The ledger records idempotent charge IDs; initial usage
cannot be injected without a charge record.

## Deterministic and generated orchestration

`GameAgent.Workflow` provides durable step, parallel DAG, foreach, reduce, and
bounded-loop stages with schemas, leases, checkpoints, cancellation, deadlines,
stable child identities, and recovery.

`GeneratedPlanCompiler` is the narrower model-authored surface. It admits only
commands that the game registered beforehand. Generated JSON cannot register
code, tools, providers, or new executor kinds. It supports ordered and parallel
commands, bounded foreach, reduce, bounded feedback loops, duration metadata,
and durable wait/signal patterns through admitted host commands. The compiler
rejects unknown properties, unknown commands, bad schemas, cycles, unbounded
expansion, and invalid pointers before execution. Host receipts use stable
execution IDs so interrupted side effects can be reconciled without replay.

For open-ended work, use the normal Agent loop and let each authoritative tool
result become the next observation. For repetitive deterministic work, compile
an admitted plan so one model decision can drive many bounded host operations.

## Tools, skills, and extensions

Tools are immutable, schema-bound capabilities with effect type, conflict
scope, thread affinity, timeout, visibility, and idempotency declarations.
Deferred tools can be searched and activated within bounded disclosure budgets.

Skills are progressively disclosed instruction/resources packages. Local skill
packages are bounded, reject unsafe paths and links, and are activated through
a host-controlled admission policy. Declarative extension manifests can bind
existing skills, tool schemas, workflows, providers, context contributors, and
resources. The declarative catalog never loads assemblies, native libraries, or
executable payloads; every reference resolves against a registry owned by the
game.

## Media and generated game content

`GameAgent.Generation` supplies provider-neutral jobs for image, video, speech,
and structured-content APIs. Providers may be remote services or local HTTP
endpoints; the repository bundles no generation model. The runtime provides:

- idempotent operation IDs and request digests;
- asynchronous submission, polling, cancellation, progress, and restart
  recovery;
- bounded streaming speech that never mixes providers after audio begins;
- artifact size, host allowlist, redirect, digest, and file-signature checks;
- local artifact import before an expiring provider URL is considered durable;
- pre-dispatch uncertainty fencing and durable materialization-source recovery;
- a stage, validate, commit, abort, and reconcile transaction for generated
  content entering the game.

Generated scripts are transported as inert source assets. They execute only if
the game explicitly validates, sandboxes, and commits them; the runtime never
loads them as code.

The Agent can use generation through ordinary registered tools, while Godot and
Unity also expose typed engine-facing generation APIs and completion events.

Read [media and generated content](media-and-generated-content.md) for the
integration boundary.

## Engine boundary

Godot and Unity adapters run the shared runtime in the game process, marshal
engine mutations onto the main thread, bound queues and per-frame work, publish
completion/fault events, and own asynchronous shutdown. Shared behavior remains
in `netstandard2.1` libraries rather than being reimplemented per engine.

## Deliberately outside the framework

- game rules, numerical balance, permissions, and save schema;
- concrete movement, building, combat, inventory, or economy implementations;
- rendering, navigation, animation, and user interface;
- a bundled local model or commercial credential service;
- an end-user world browser, launcher, or content marketplace;
- engine integrations other than Godot and Unity in this release scope.

These are product or game responsibilities. Keeping them outside the runtime is
what lets one authoritative execution contract support very different games
without reducing all games to one world model.
