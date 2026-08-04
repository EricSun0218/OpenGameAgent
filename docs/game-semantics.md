# Game semantics

A game agent is not a chat session attached to an NPC. It is a decision process
inside a simulation. The runtime therefore treats game state as explicit,
structured coordinates and keeps operational concerns separate from simulation
meaning.

## Semantic coordinates

`GameContextCoordinate` identifies the slice of the simulation that an agent
observed:

- `worldId` prevents data crossing between worlds or campaigns;
- `timelineId` separates branches, alternate histories, and parallel worlds;
- `saveRevision` separates committed save progress;
- `stateVersion` supports optimistic validation against a world snapshot;
- `GameTimePoint` names a game-defined clock, timeline, epoch, and tick;
- `GameEntityIdentity` combines an entity ID with an incarnation;
- observer, scene, and region fields bound perspective and spatial scope;
- `GameCausalityStamp` records an event, its parents, and the state version it
  observed.

The coordinate is stored in the run's `gameContext` extension and copied to
host action requests. It does not assume that a tick is a frame, second, turn,
day, or any other fixed unit.

`epoch`, `tick`, save revision, and state version use exact integers because
they are ordering and identity fences. That does not restrict game data to
integers: ordinary context, workflow values, tool arguments, receipts, and
outputs accept bounded JSON numbers, including fractions, negative values, and
scientific notation. A game may use decimal strings or fixed-point integers
when its own economy needs an exact cross-platform arithmetic contract.

## Advancing authoritative game context

The run coordinate is a durable fence, not a one-time launch parameter. After
the game commits an action, it can attach the authoritative post-action
coordinate to the terminal receipt:

```csharp
var receipt = new ActionReceipt
{
    OperationId = request.OperationId,
    Revision = 1,
    Status = ReceiptStatuses.Succeeded,
    ReceivedAt = now,
    CommittedAt = now
};

GameContextReceiptEnvelope.AttachResulting(
    receipt,
    resultingCoordinate);
```

The optional `previous` argument is an explicit assertion about the decision
window's baseline. It does not describe an intermediate step. If several
terminal receipts belong to one model decision, every receipt that supplies a
resulting coordinate must report the same final coordinate. A sequence of
state transitions must be split across later provider turns so each new action
request is fenced to the newly committed state.

Receipt coordinates may omit `sessionId`; the runtime binds a missing value to
the immutable session of the current run before persisting and comparing the
receipt. An explicitly supplied session must match that run exactly or receipt
ingress fails closed.

The runtime advances only after every action in that decision has a terminal
receipt. `unknown` receipts never advance the coordinate; recovery may advance
only after reconciliation returns a terminal receipt. A receipt without
`resultingGameContext` preserves the existing `gameContext` JSON byte-for-byte.

An advance is accepted only when:

- world, timeline, run session, observer entity, and observer incarnation are
  unchanged;
- save revision does not decrease;
- state version is not removed;
- game clock and game-time timeline are unchanged, and `(epoch, tick)` does not
  move backward; a higher epoch may restart its tick;
- an existing causal event is not rewritten, and a new causal event names the
  previous event as a parent;
- the action request's complete source coordinate and
  `basedOnStateVersion` match the durable pre-action coordinate.

The runtime journals `game_context_advanced` as one atomic semantic event and
full run checkpoint after the supporting receipts and before memory policy or
the next provider request. The event binds the previous coordinate, resulting
coordinate, sorted operation IDs, and deterministic event identity. Recovery
revalidates that evidence against the journaled action requests and terminal
receipts and fails closed on tampering. The newly committed coordinate is
therefore visible to receipt-driven memory policy, next-turn recall, and every
later action request.

Godot and Unity adapters preserve the receipt extension as ordinary structured
protocol data. The game remains responsible for computing the authoritative
coordinate; adapters do not infer simulation state from engine frames or scene
objects.

## Separate kinds of time

These clocks must not be substituted for one another:

| Time domain | Examples | Runtime responsibility |
| --- | --- | --- |
| Operational wall time | network deadlines, cancellation, credential expiry, billing windows | enforce bounded waits and resource lifetimes |
| Monotonic process time | timeout measurement while the process is alive | prevent wall-clock changes from extending work |
| Simulation time | seasons, turns, NPC age, dream time, local time dilation | carry game-defined coordinates without interpreting them |
| Causal order | event parents, world versions, simultaneous decision windows | preserve and propagate evidence |
| Presentation time | dialogue reveal speed, animation, UI coalescing | emit frame-friendly chunks without changing simulation state |

Pausing, speeding up, rewinding, or loading a save changes simulation
coordinates. It must not extend a network deadline. Conversely, real-world time
passing must not age an NPC memory unless the game explicitly maps that passage
into its simulation clock.

An epoch distinguishes resets that reuse tick numbers. Two time points compare
only when clock, timeline, and epoch all match. `GameTimeWindow` uses an
inclusive lower and exclusive upper bound. Wall-clock `expiresAt` remains useful
for operational cache eviction; it is not a substitute for a game-time validity
window.

## Knowledge is perspectival

The model receives a view, not omniscient world state. Memory provenance can
carry `GameKnowledgePerspective`, which identifies:

- the observing entity and its incarnation;
- a game-defined knowledge kind such as witnessed, reported, inferred, rumored,
  dreamed, or fabricated;
- an optional source entity.

The runtime isolates perspectives but never declares one belief true. A game can
let two NPCs remember incompatible accounts of the same event. Authoritative
world facts, private observations, beliefs, and dialogue claims should remain
different typed context items.

Memory recall is fail-closed for perspectival records. A `MemoryQuery` without
an `Observer` excludes every record carrying a `GameKnowledgePerspective`.
Supplying an observer includes only that entity incarnation's perspectival
records. Records without a perspective remain available in both cases, so
shared world facts do not need to be copied into every NPC's memory.
`IncludeAllPerspectives` is an explicit privileged option and defaults to
`false`; games should enable it only for trusted system-level operations such as
administration, migration, or debugging, never for ordinary actor recall.

Entity incarnation is required wherever IDs can be reused. A respawned,
possessed, cloned, or reincarnated entity does not silently inherit memories or
pending work from an earlier lifetime merely because the string ID is the same.

The same fence is available for live observations. Protocol audience IDs do
not have to equal game entity IDs: one
`ObservationAudienceIncarnationBinding` explicitly links an audience ID to a
`GameEntityIdentity`. For games where IDs can be reused, attach a complete,
bounded binding set to every non-public observation and enable:

```csharp
var options = new DurableAgentRuntimeOptions
{
    RequireAudienceIncarnationForRestrictedObservations = true
};

ObservationAudienceIncarnations.Attach(
    observation,
    new[]
    {
        new ObservationAudienceIncarnationBinding(
            "agent-17",
            new GameEntityIdentity("npc-42", incarnation: 3))
    });
```

The run's `GameContextCoordinate.Observer` must then be the same `npc-42`
incarnation. A newly spawned `npc-42` with incarnation `4` cannot receive
incarnation `3`'s private context even if the protocol `agentId` was reused.
The admission metadata survives candidate cloning and durable run-input
recovery.

## Concurrent actors and simultaneous actions

Games commonly ask many actors to decide against one world snapshot. A single
shared transcript is unsafe: it leaks private knowledge, couples failures, and
makes scheduling order alter behavior.

`MultiActorDecisionCoordinator` runs one durable decision per actor with:

- a bounded batch size and bounded concurrent run count;
- fail-fast limits for active batches and total admitted participants;
- per-run and aggregate snapshot byte/node budgets before work starts;
- an optional aggregate token, action, duration, and cost ceiling reserved from
  every participant's hard run budget before lifecycle or provider work starts;
- a manifest returned with every admitted batch outcome, including partial
  failures, so save data never has to reconstruct participant identities;
- one immutable `GameContextCoordinate` shared by the decision window;
- unique run, actor, and decision identifiers;
- failure isolation between actors;
- deterministic result ordering independent of completion order;
- unchanged caller requests; batch metadata is attached to snapshots;
- propagation of `batchId`, `decisionKey`, and `basedOnStateVersion` to every
  host action.

The shared coordinate supplies the world snapshot, timeline, session, save
revision, state version, game time, and causality. Its `sessionId` must exactly
match every participant run, including whether the value is absent. A
participant may attach its own coordinate with the same shared fields to
preserve its observer incarnation and scene/region perspective. A
multi-participant batch rejects a single observer on the shared coordinate
because that identity cannot describe every actor.

Games should classify an off-screen simulation batch as `background` when they
configure a background provider quota. A visible group conversation can remain
`interactive`; the coordinator does not guess importance from the fact that
several actors participate.

`MultiActorBatchBudget` is admission control, not post-hoc accounting. The
coordinator sums the participants' declared hard run budgets and rejects the
entire batch if any aggregate ceiling would be exceeded. Exact reserved totals
and limits are returned in the manifest, so a save, telemetry pipeline, or host
scheduler can audit the decision without recomputing it. Omitting the aggregate
budget preserves per-run limits only.

The repository performance smoke also measures bounded coordination of 64
actors. It measures runtime snapshotting and scheduling overhead, not model,
game-host, rendering, or network capacity; each game must benchmark those parts
with its real decision cadence.

Actors may think concurrently. Their actions are proposals until the game host
returns receipts. The game can resolve them immediately, stage all actions in a
batch, or apply its own initiative, lockstep, transaction, or rollback system.
The runtime does not decide whether two attacks, trades, movements, or dialogue
interruptions conflict.

Games whose action loop requires one mutation followed by one authoritative
receipt can set `MaxSideEffectToolCallsPerTurn = 1`. This does not serialize NPC
thinking or prohibit parallel reads. It atomically rejects all side-effecting
calls when one model response exceeds the limit, while valid reads may still
run; the model then replans on the next turn from typed results.

A host that stages actions can implement `IMultiActorDecisionLifecycle`.
`BatchStartedAsync` receives the complete expected participant manifest before
any actor starts. `ActorFinishedAsync` marks an actor that will submit no more
actions, including a failed or no-action actor. This lets the host resolve a
window once every participant has either submitted an action or finished,
without assuming that every agent will call a tool. If cancellation or an
infrastructure failure prevents normal closure, `BatchAbortedAsync` tells the
game to discard or reconcile the proposals already staged for that batch. An
abort is also sent when batch startup throws because the coordinator cannot
know whether the host staged the manifest before failing. Every lifecycle
callback must be idempotent by batch and run identifier because recovery can
repeat a notification whose previous outcome was uncertain. Abort notification
has its own configurable settlement deadline and bounded detached-work
capacity. If the host does not confirm in time, the batch failure includes a
`MultiActorBatchAbortUncertainException` so the game can reconcile the staged
window without hanging cancellation.

Coordinators that share one runtime also share an in-process batch fence, so
the same batch identifier cannot execute concurrently through two coordinator
instances. A multi-process host must make its lifecycle implementation a
durable ownership boundary: `BatchStartedAsync`, actor completion, and abort
must compare an owner or attempt generation before accepting a write. The
Use `WorkflowRunner` for fixed world-evolution workflows that need its durable
lease and owner-generation fence. A custom multi-process staging service must
provide the equivalent guarantee.

Nonterminal participants are not retained in coordinator memory. Their batch,
actor, decision, and input-order metadata travels with the durable run, so
`ResumeParticipantAsync` accepts the persisted
`MultiActorBatchParticipant` descriptor after constructing a new coordinator or
restarting the process. Before ownership, provider, reconciler, or host work,
the runtime atomically verifies the descriptor's batch, run, actor, decision
key, and input index against the journal. Concurrent resumes are bounded and
duplicate operations on the same participant are rejected within one
coordinator. A runtime resume failure does not close the lifecycle window; the
game can retry after correcting the transient failure.

Identity is not enough when the world can advance while an actor is paused.
Build a `DurableRunSemanticExpectation` from the game's current coordinate and
pass it to the semantic `ResumeParticipantAsync` overload. The coordinator
merges that caller-owned expectation with its identity guard; it never derives
the current expectation from the old manifest. A missing extension or digest
mismatch fails before ownership, provider, reconciler, or host work:

```csharp
var current = GameContextEnvelope.ToJson(currentCoordinate);
var expectation = DurableRunSemanticExpectation.FromJson(
    GameContextEnvelope.ExtensionName,
    current);

MultiActorRunResult resumed = await coordinator.ResumeParticipantAsync(
    manifest.BatchId,
    participant,
    expectation,
    continuation,
    reconciler,
    cancellationToken);
```

`CanonicalJsonDigest` ignores object-property order but preserves array order
and JSON number representations. Its public trust boundary rejects undefined,
duplicate-property, oversized, over-deep, and over-complex JSON before
canonical materialization.

If the game permanently discards an actor, it calls
`ReconcileAbandonedParticipantAsync` with that same manifest descriptor. This
performs a guarded durable cancellation rather than merely notifying the host.
Pending game operations must first reach authoritative reconciliation;
`ActorFinishedAsync` is sent only after the run is terminal. Retrying terminal
resume or abandonment safely replays the idempotent finish notification, which
also recovers a prior callback whose acknowledgement was lost.

For simultaneous resolution, a host should:

1. capture one immutable world snapshot and coordinate;
2. start actor runs with unique decision keys in one batch;
3. stage action requests by `batchId`;
4. reject or recompute actions whose `basedOnStateVersion` is stale;
5. resolve conflicts using game rules;
6. commit world mutations atomically where the game requires it;
7. return one authoritative `ActionReceipt` per operation.

Conflict keys still protect runtime tool execution. They are scheduling
metadata, not a replacement for game legality or transaction rules.

## Other chat-agent assumptions to avoid

### One user and one linear conversation

A world has many actors, players, scenes, quests, factions, and services.
Durable state belongs to explicit world, actor, session, and timeline scopes.
Provider-facing transcript compaction is a derived view, never the authoritative
world record.

### Natural language is the state

Observations, triggers, memories, tool arguments, and results may be JSON,
numbers, enums, event payloads, IDs, or resource references. Prose is one
presentation format. Structured state remains authoritative.

### Tool execution is the source of truth

The model proposes a typed action. The game validates permissions, topology,
inventory, cooldowns, ownership, multiplayer authority, and all other business
rules. Only an `ActionReceipt` proves a committed result.

### Every call should complete immediately

Gameplay may pause an agent, stream a scene out, wait for a turn boundary, or
lose connectivity. Runs and pending operations are durable. A game should
schedule decisions asynchronously and never block its render or simulation
thread on model latency.

### Process lifetime equals character lifetime

Save/load, hot reload, engine shutdown, world streaming, and crashes are normal
boundaries. Persist semantic coordinates and durable events; treat in-memory
caches, cancellation tokens, and wall-clock deadlines as replaceable process
state.

### More context is always better

An NPC should see only context allowed by its perspective, spatial reach, game
rules, and current state version. Large worlds require selection, resource
references, prefetch, and level-of-detail scheduling rather than global prompt
dumps.

### One model call per NPC per frame

That cost and latency model does not scale. Games should combine event-driven
triggers, decision cadence, distance or importance tiers, deterministic
non-model behaviors, cached plans, and bounded batch scheduling. The runtime
provides budgets and backpressure; the game chooses who needs cognition now.

### Provider output is deterministic

Model output may vary even with identical input. Store the exact prompt view,
tool/skill generation, route, usage, host receipts, and resulting trace.
Deterministic replay should replay recorded decisions and receipts; re-running a
model is evaluation, not proof of identical simulation.

### Schemas and content never change

Mods, downloadable content, live updates, and save migrations can change tools,
skills, resources, and entity schemas. Version game-owned content, snapshot the
effective catalogs per turn, and migrate durable game data explicitly.

## Ownership boundary

| Concern | Runtime core | Engine adapter | Game layer |
| --- | --- | --- | --- |
| Provider loop, retries, budgets | owns | pumps events | configures policy |
| Structured context selection | bounds and snapshots | converts engine data | decides visibility and meaning |
| Game clocks and timelines | carries coordinates | reads engine clocks | defines units, forks, and rewinds |
| NPC memories | stores provenance and filters | supplies save lifecycle | defines formation, decay, and belief changes |
| Multi-actor workload | bounds and isolates runs | schedules off frame thread | selects actors and decision windows |
| Action conflicts | propagates keys and versions | dispatches safely | adjudicates and commits |
| Spatial scope | carries scene/region IDs | observes streaming | defines topology and reachability |
| Save/load and replay | journals runtime evidence | coordinates shutdown/load | owns authoritative world snapshots |
| Multiplayer authority | never assumes authority | bridges transport/main thread | validates server or peer authority |

This boundary keeps the runtime reusable without reducing game semantics to chat
history or hard-coding one title's simulation rules.
