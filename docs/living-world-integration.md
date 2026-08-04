# Living-world integration

This guide assembles the runtime primitives needed for a persistent campaign,
social simulation, or character-driven sandbox. It covers conversations that
change the world, private knowledge, autonomous characters, delayed messages,
group scenes, evolving events, high-level tactical decisions, and generated
media.

The runtime supplies cognition, durability, isolation, scheduling primitives,
and action evidence. The game continues to own simulation rules, pathfinding,
combat control, economy, relationships, saves, and UI.

This is a composition guide. The normative contracts remain in
[the runtime capability model](runtime-capability-model.md),
[game integration patterns](game-integration-patterns.md),
[group interactions](group-interactions.md), and
[media and generated content](media-and-generated-content.md).

## Capability map

| Experience | Runtime composition | Game-owned component |
| --- | --- | --- |
| Character dialogue with consequences | durable Agent route, typed context, tools, receipts, memory policy | dialogue UI, relationship rules, legal mutations |
| Personality, history, and speaking style | structured profile context, scoped memory, Direct generation | profile schema, editing and migration |
| Public facts, secrets, rumors, and lies | provenance, perspective, tags, game-time windows | knowledge acquisition, truth and disclosure policy |
| Persistent errands and travel orders | Agent plan, saved intent, game-time trigger, mailbox, reconciliation | navigation/state machine and task legality |
| Delayed letters or messengers | game-time delivery record, trigger, durable mailbox | distance, cost, interception, delivery rules |
| Group and ambient conversations | group interaction, exact audience, independent Agents, bounded batch | proximity, speaker selection, pacing, subtitles |
| Autonomous character initiative | persistent Agent identity, residency, background workload, budgets | eligibility, cadence, importance and distance tiers |
| Dynamic world events | structured generation, durable workflow, triggers, scoped memory | event schema, propagation, effects and expiry policy |
| Diplomacy, quests, economy, and disease | closed-schema tools, workflows, receipts, external attention | domain state, permissions, formulas and transactions |
| Tactical commands | Direct or Agent route, typed snapshot, high-level command tools | formations, behavior trees, frame-level control |
| Images, video, and voice | generation jobs, artifacts, streaming speech, content transaction | prompt policy, presentation, lip sync and asset import |

## One character decision

Route and supervision semantics are defined by
[the routing guide](how-to-route-and-supervise-agents.md). This section only
applies them to a living-world decision.

Use one exact `GameContextCoordinate` for the decision. Include the world,
save revision, state version, timeline, game time, observer, and observer
incarnation. Add bounded typed observations for the actor's visible state,
relationships, current intent, nearby entities, known facts, and legal action
IDs.

Choose the least expensive route that preserves the required guarantees:

- `Completion` for isolated classification, extraction, rewriting, or profile
  generation that needs no durable state;
- `Direct` for durable dialogue or one structured decision with no tools;
- `Agent` when the model may inspect, act, observe a receipt, and replan;
- `Workflow` for a fixed recoverable sequence, bounded fan-out, loop,
  reduction, or external wait around Agent steps.

Text is presentation. A world change is true only after a game-owned tool
handler validates and commits it and returns an `ActionReceipt`.

## Knowledge and memory

The storage, retrieval, and isolation contracts are maintained in
[tools, skills, and memory](tools-skills-memory.md) and
[game semantics](game-semantics.md).

Model distinct concepts as distinct records:

- an authoritative public event;
- one character's observation of that event;
- a rumor derived from another speaker;
- a deliberate claim or lie;
- a dialogue episode;
- a distilled long-term memory.

Use `GameKnowledgePerspective` and entity incarnation for private beliefs.
Use tags and scopes for categories and audiences. Use `GameTimeWindow` for
knowledge that becomes valid or expires in simulation time; wall-clock
`ExpiresAt` is reserved for process or service concerns.

Local BM25 recall requires no embedding model. A game can add an embedding
provider and hybrid retrieval when semantic recall is valuable. Retrieval
must never broaden world, session, timeline, perspective, tag, or result
limits.

Knowledge distribution remains game policy. For example, a game may assign a
secret when a character is created, propagate news by region and travel time,
or disclose it only above a trust threshold. The runtime persists and filters
the resulting records but does not define those rules.

## Persistent orders and delayed messages

Keep long-lived intent in game-owned save data. A useful record contains:

- a stable intent and actor-incarnation ID;
- the goal and validated target references;
- status and current low-level state-machine phase;
- the next eligible game time;
- the originating decision key and last run ID;
- the world, timeline, and state version used to decide;
- the last authoritative command receipt.

The model chooses a high-level order. A game state machine performs movement,
waiting, combat, and animation without blocking a model call. A later game
event or time occurrence starts a new decision with the updated intent and
observation. Stale coordinates cause rejection or replanning.

A delayed message uses the same pattern: the game commits sender cost and
delivery time. When that time is reached, the game calendar submits a
host-provided occurrence. The trigger coordinator persistently admits,
deduplicates, and applies catch-up and overlap policy; it does not advance the
clock or wake itself. The host consumes an admitted launch and idempotently
enqueues a durable mailbox message.

Interception, range, travel speed, and whether a reply is allowed are ordinary
game rules. The game must also validate the recipient incarnation. If delivery
changes game state, use one game-save transaction for that effect and its
acknowledgement, or an idempotent consumer/outbox keyed by the stable message
ID. Marking a mailbox entry delivered is not by itself an exactly-once game
transaction.

## Group and ambient conversations

Use the revision, audience, and identity rules from
[group interactions](group-interactions.md); the steps below describe the
living-world composition around that primitive.

Create a `GroupInteractionSession` with exact participant incarnations and a
world binding. Append approved public lines with their exact audience. Build
each speaker's request from that participant's projection plus private
context; never merge all private prompts into one conversation.

The game selects nearby participants, speaking order, directed replies,
interruptions, cooldowns, and maximum exchanges. Use a bounded multi-actor
batch only when several characters decide against the same immutable world
snapshot. Persist an overheard line as a perspective-scoped memory with an
optional game-time validity window.

Ambient conversation should be event-driven and level-of-detail aware. A
proximity check can select one eligible pair, start a short session, and place
both Agents on cooldown. Characters outside the player's relevance radius can
continue through deterministic simulation rather than model calls.

## Dynamic events and world evolution

Represent a generated event as a closed proposal, not free-form authority. A
typical schema includes type, title, description, importance, involved entity
IDs, affected regions, start and end game times, visibility rules, and bounded
effect proposals.

A recoverable event pipeline can:

1. capture one immutable world snapshot;
2. use `Direct` or an Agent step to propose a typed event;
3. validate IDs, ranges, permissions, and conflicts in game code;
4. commit the event and effects through idempotent commands;
5. propagate perspective-scoped observations to eligible characters;
6. optionally submit image, speech, or video generation jobs;
7. have the host consume bounded trigger launches for affected Agents;
8. expire the event or its derived memories in game time.

Use a workflow only when these stages must recover as one orchestration. A
monthly or daily calendar remains game-owned and submits occurrences to the
game-time trigger coordinator.

## Domain actions

Diplomacy, quests, trade, taxes, territory, relationships, inventory, disease,
and other systems use the same contract:

1. project the actor's authority and available choices;
2. expose narrow verbs with closed JSON Schemas;
3. require target IDs rather than guessed names;
4. validate role, ownership, range, cooldowns, balances, and state revision;
5. atomically commit the domain mutation;
6. return the exact receipt and resulting observation;
7. let the Agent explain, continue, or replan from that evidence.

Multi-step orders can use the bounded generated-plan compiler when the command
catalog and schemas are host supplied. A game that already has a quest or task
state machine may instead expose create, update, cancel, and inspect tools and
keep the plan in its existing save format.

## Tactical decisions

LLM latency is appropriate for commander intent, negotiation, target
selection, formation posture, or decisions made every several seconds or at a
phase boundary. It is not appropriate for per-frame movement, aiming,
collision, animation, or network reconciliation.

Capture a bounded tactical snapshot, use `Direct` for a single structured
order or `Agent` when reads and replanning are useful, then pass admitted
commands to the existing behavior tree or tactical controller. Player text or
voice commands should first become typed intent; voice recognition is an
external input service and is not bundled with the runtime.

## Generated media

Provider and artifact details are maintained in
[media and generated content](media-and-generated-content.md).

Dialogue portraits, event art, narration, voices, and other assets use the
generation subsystem independently of the Agent loop. Persist job acceptance
before polling, verify imported artifacts, and ask the game to validate a
content transaction before exposing the result.

The runtime can call local or remote APIs and does not bundle models. Streaming
speech is supported. Speech-to-text, lip sync, camera capture, and visual Agent
input are game or provider integrations; submit their admitted transcript or
typed result as an observation.

## Save and recovery model

Keep these boundaries explicit:

- authoritative world and simulation state: game save;
- runtime decision evidence and action receipts: runtime journal;
- character beliefs and derived history: memory store or game save;
- group transcript and membership evidence: group store;
- long-lived actor identity and pending messages: persistent Agent graph;
- fixed orchestration progress: workflow store;
- generated media jobs and imports: generation stores.

Bind every durable record to the world/timeline identity needed to reject a
different save, fork, rewind, or respawn. On load, reconcile uncertain action
receipts before starting a new decision that depends on them.

For a persistent mailbox, make the Agent ID incarnation-specific or carry the
recipient incarnation in the payload and validate it before consumption.

## Acceptance gate

First pass the
[retrofit acceptance gate](game-integration-patterns.md#retrofit-acceptance-gate).
Then verify these living-world additions:

1. Public facts and two characters' private beliefs remain isolated, and a
   belief can expire according to game time rather than real time.
2. A delayed message survives save/load, is admitted idempotently by message
   ID, validates the recipient incarnation, and cannot repeat its game-owned
   delivery effect after a crash.
3. A persistent order resumes from game state instead of replaying narration.
4. A periodic world event catches up according to an explicit policy, and its
   effects are reconciled after an unknown result.
5. Generated assets remain hidden until artifact and content validation pass.

Passing this gate proves the runtime integration. The quality of dialogue,
simulation rules, content, UI, and low-level AI remains the game's work.
