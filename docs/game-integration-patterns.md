# Game integration patterns

Game Agent Runtime adds deliberation to an existing game architecture. It does
not replace the simulation, behavior tree, state machine, save system, or
multiplayer authority. The fastest integration keeps those systems intact and
adds three narrow seams:

1. project bounded, perspective-correct game state into typed context;
2. expose selected game commands as closed-schema tools;
3. return an authoritative receipt after game code validates and commits a
   command.

This document describes reusable patterns for character-driven simulations,
AI dialogue games, systemic sandboxes, and event-driven narrative games. The
field names in examples belong to the game, not to the runtime.

## Split cognition from execution

Use the model for decisions whose value comes from interpretation, planning,
negotiation, or expression. Keep low-level movement, animation, combat timing,
navigation, collision, and frame-by-frame reactions in deterministic game AI.

```text
game event or decision cadence
  -> typed observation for one perspective
  -> Agent Runtime chooses a directive or calls a tool
  -> game validates and commits
  -> behavior tree / state machine executes low-level behavior
  -> receipt and new observation return to the Agent Runtime
```

For example, an Agent may choose `visit`, `negotiate`, `avoid`, or `betray`
with a target and constraints. A behavior tree can then pathfind, animate, and
retry local movement without another model request. Do not call a model once
per NPC per frame.

## Conversational character with real actions

Build one `AgentRun` per acting character and attach a
`GameContextCoordinate` containing its world, save revision, timeline, game
time, state version, and observer incarnation. Context can include structured
relationship values, visible entities, permitted action IDs, recent public
dialogue, and character-private beliefs.

Expose narrow tools such as `offer_item`, `request_information`, or
`change_stance`. The tool handler checks ownership, location, permissions,
cooldowns, and all other game rules. Assistant text is presentation; only the
handler's `ActionReceipt` can prove that state changed.

When dialogue itself is the only output, use final structured output and a game
presentation layer. Do not invent a fake write tool merely to save dialogue.
`IRuntimeMemoryPolicy` can derive dialogue memory after the assistant output is
durably committed.

## Shared scenes and group dialogue

Use `GroupInteractionSession` for a bounded shared transcript and membership
history. Project a different view for each participant, then run independent
character Agents. This preserves character identity and failure isolation
while allowing everyone in a scene to observe the same public exchange.

Private memory is never copied into the group transcript automatically. The
game decides whether a private fact is spoken, summarized, or withheld. Use
`MultiActorDecisionCoordinator` when several participants decide against one
immutable simulation snapshot; do not merge their private prompts into one
model session.

## Game-time and event-driven decisions

The game remains the scheduler. A day change, month change, quest event,
proximity event, combat phase, or player interaction starts a run or a bounded
multi-actor batch. Put the simulation clock in `GameTimePoint`; wall-clock
timeouts continue to protect network and tool operations independently.

For a periodic background update:

1. select only characters that currently merit model cognition;
2. capture one immutable coordinate and per-character perspective;
3. mark each request as `ProviderWorkloadClasses.Background`;
4. reserve an aggregate `MultiActorBatchBudget`;
5. let the game stage or validate proposals using its own conflict rules;
6. persist receipts and the resulting coordinate before the next decision.

Use `GameAgent.Workflow` only when the event itself has a fixed recoverable
sequence, bounded loop, fan-out, or reduction. A game calendar and event rules
do not belong in the runtime.

## Long-lived goals and plans

Store long-lived intent in game-owned save data. A compact record commonly
contains an intent ID, actor incarnation, goal, target references, status,
next eligible game time, last run ID, and the state version on which the plan
was based. Feed the current record back as typed context when the game decides
that the actor should think again.

Use a stable `DecisionKey` for one logical decision window so retries do not
reroll a committed result. Use durable resume for an interrupted Agent run;
start a new run when the game intentionally opens a new decision window.
Always reject or replan an intent whose save, timeline, incarnation, or state
coordinate is stale.

## Character memory and knowledge

Keep world facts, observed events, beliefs, rumors, fabricated claims, and
dialogue text as distinct game-defined record types. Attach
`MemoryProvenance`, `GameKnowledgePerspective`, and optional game-time windows.
Perspective filtering prevents a reused entity ID or another character from
silently receiving private memory.

Use local BM25 memory when deterministic lexical recall is enough. Add a
game-supplied embedding provider and reciprocal-rank fusion when semantic
recall is worth the model, storage, and migration cost. Memory remains derived
context: it cannot settle a pending action or override authoritative state.

## Generated items, abilities, and narrative proposals

Treat generated content as a proposal with a closed schema. The game provides
the allowed mechanic IDs, numeric ranges, tags, and current constraints. A
tool handler validates the proposal, clamps or rejects it according to game
rules, assigns the authoritative content ID, commits it, and returns a receipt.

This same pattern covers rewards, item descriptions, quests, relationship
changes, schedules, and high-level behaviors. Free-form text may decorate the
result but must not carry hidden mechanics that bypass validation.

## Multiplayer and commercial clients

For an authoritative multiplayer game, run mutation-capable Agent work on the
server or require every client proposal to pass through server-owned handlers.
The engine-embedded runtime does not make a client authoritative and does not
protect a permanent provider secret shipped in a player build.

An offline or player-supplied-key game can run the whole loop locally. A game
that funds model usage should exchange game authentication for short-lived,
scoped access through a service it controls. This deployment choice does not
change the tool and receipt boundary.

## Engine mapping

- Godot: host the runtime in the Autoload `GameAgentRuntimeNode`, do provider
  work off the frame thread, and dispatch engine-object access through the
  bounded main-thread dispatcher.
- Unity: host the shared runtime through `GameAgentRuntimeBehaviour` and the
  durable backend; use Unity's main-thread dispatcher only for engine API
  access.
- Unreal: keep observations, actions, receipts, and semantic coordinates on
  the portable JSON/wire boundary. The current alpha supplies the C++/C ABI and
  GameThread compatibility surface, not a complete native backend.

## Capability acceptance matrix

| Game need | Runtime primitive | Game responsibility |
| --- | --- | --- |
| Dialogue that changes state | typed tools, action journal, receipts | dialogue UI, legality, mutation |
| Multiple characters in one scene | group interaction plus independent runs | visibility and speaking rules |
| Concurrent NPC decisions | multi-actor batch, budgets, deterministic result order | actor selection and conflict resolution |
| Day/month/event evolution | background workload and optional durable workflow | calendar and trigger rules |
| Private and mistaken memories | perspective, incarnation, provenance, time window | memory formation and truth model |
| Persistent plans | durable run/resume, decision key, semantic guard | saved intent schema and eligibility |
| Generated abilities or items | structured proposal and authoritative receipt | mechanic pool, balance, content persistence |
| Fast player-visible streaming | stale-stream fence and presentation coalescer | UI layout, animation, voice |
| Crash or timeout during a write | write-ahead request and reconciliation | operation lookup and idempotent commit |
| Large tool catalog | deferred disclosure and skill admission | capability design and trust policy |

## First vertical slice

A credible first slice has one structured trigger, one character perspective,
one read tool, one mutation tool, one committed receipt, one saved run, and one
restart/reconciliation test. Keep the provider deterministic until that path
works. Then add a real model, memory, shared interactions, and background
batches in that order.

An existing game is ready for this slice when it can expose a bounded state
projection, call a game-owned command handler, and provide a persistent data
directory. Character stats and behaviors do not need to be redesigned around
the runtime.

## Retrofitting an existing game

Do not replace an existing dialogue manager, behavior tree, event system, or
save format. Add a thin anti-corruption layer at the three seams described at
the start of this document. This keeps the first integration reviewable and
lets the game remove the Agent feature without corrupting its save.

For a scene-driven character game, the first adapter normally maps:

- the selected speaker and nearby participants to per-character observations;
- existing relationship, inventory, location, and permission data to typed
  context;
- existing command methods to tool handlers and receipts;
- public dialogue to a group session while retaining private character memory;
- the current save slot, world revision, and character lifetime to a
  `GameContextCoordinate`.

For an event-driven simulation, the first adapter normally maps:

- an existing event or simulation tick to a run trigger;
- the actor, goal, numeric state, and legal high-level choices to structured
  context;
- an Agent proposal to the existing state machine, behavior tree, or event
  command API;
- the committed command result to a receipt and the next observation;
- day, turn, season, or month counters to a named game clock rather than wall
  time.

The integration is still thin when all game-specific DTO conversion and rules
fit in the game repository. If a proposed change requires adding a particular
game's stats, relationship formula, event catalog, or save objects to the
runtime, keep that change in the adapter instead.

### Retrofit acceptance gate

Before calling an existing game supported, verify all of the following:

1. The Agent can consume a fully structured observation with no natural-language
   requirement.
2. A real read and a real mutation travel through existing game APIs.
3. Rejected and unknown mutations return distinct receipts and do not become
   narrated facts.
4. A process restart does not repeat an already committed side effect.
5. Two characters can act concurrently without sharing private context.
6. A shared scene preserves an exact participant revision and audience.
7. A game-time event can wake a bounded background batch without depending on
   the operating-system clock.
8. A stale save, timeline, state revision, or entity incarnation is rejected.
9. Low-level movement and frame-critical behavior continue through the game's
   existing deterministic AI.
10. Provider credentials follow the game's deployment model and are not
    embedded as a permanent commercial secret.

When these checks pass, adding more NPC types or event kinds is primarily game
content and adapter work, not a new runtime architecture.
