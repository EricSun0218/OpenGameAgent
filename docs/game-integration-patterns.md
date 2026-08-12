# Game integration patterns

The framework stays generic by standardizing agent execution, not gameplay. This page shows how common AI-game features map onto existing game systems.

## Conversational NPC

Input includes the player's utterance plus non-language interaction state. Context includes identity, visible scene, relationships, recent events, and recalled memory. Read-only tools can inspect additional detail; mutation tools can accept gifts, start quests, or alter disposition through game rules.

Route ambient conversation to `QuickResponse`. Route a conversation with available actions to `Agent`. Store only durable facts or relationship changes as long-term memories; a full transcript is not automatically good memory.

## Autonomous companion

The game emits an observation when goals, threats, resources, or player orders change. Tools expose high-level capabilities such as navigate, gather, defend, revive, or build. Low-level movement and combat remain deterministic game AI or learned control code.

Use steering to inject urgent state changes during a long run. A steering message should identify the new observation version so the model can abandon a stale plan. Use conflict keys to prevent two simultaneous writes to the same companion or resource.

## Interactive world and many NPCs

Keep world simulation deterministic and cheap. Invoke a model only when a character needs semantic judgment, dialogue, planning, or content generation.

```text
game tick / month advance
  -> deterministic simulation
  -> produce bounded signals
  -> select affected actors by importance and distance
  -> enqueue one input per selected actor
  -> actors run concurrently within limits
  -> tools commit validated consequences
```

`MultiActorScheduler` gives per-actor ordering and global concurrency. `GameTimeScheduler` emits bounded recurring occurrences. `IGameMailbox` carries durable work to actors that are not currently resident. The game supplies activation, distance, importance, and budget policy.

Use `GoalLoopExtension` when an actor owns semantic goals that can wait for a tick or event and continue later. Use `AgentDelegationExtension` when one actor needs bounded background research or planning without sharing its mutable transcript. Delegates still receive explicitly scoped context and tools; delegation is not permission escalation. Delegation status can be persisted, but the included local executor runs child work in the current process and does not automatically resume an in-flight child after a process restart. Use a host-owned durable workflow or executor when child execution itself must survive restarts.

## Monthly or turn-based evolution

Represent the calendar in `GameMoment.CalendarJson` while using `Tick` for ordering. A monthly advance can be a named `DurableGameWorkflow`:

1. calculate deterministic production and upkeep;
2. identify exceptional factions or NPCs;
3. ask selected agents for decisions;
4. commit validated decisions through action handlers;
5. write memories and schedule the next occurrence.

Workflow checkpoints allow a wait between steps without losing progress. Use `agent.workflow_instance` metadata to resume the same instance intentionally.

When independent monthly branches may run together, use `DurableGameWorkflowGraph`. Dependencies are explicit, ready nodes run with bounded concurrency, joined outputs are presented in declaration order, and completed nodes are not rerun after a wait. A node that changes the world should use the durable action dispatcher with a stable operation ID because workflow and game-state storage are not automatically one transaction.

## Social deduction and group scenes

Give each actor a separate session and perspective-filtered context. Do not place secrets in a shared prompt and ask the model to ignore them. Use mailboxes or game signals for statements actors are allowed to perceive. Run independent actor turns concurrently, then resolve voting, initiative, or contested actions in deterministic game code.

## Construction and world manipulation

Building is a normal tool-planning problem. Expose tools at the safest useful level:

- `inspect_region` returns bounded geometry and constraints;
- `estimate_blueprint` checks materials and placement;
- `place_blueprint` submits a declarative plan;
- `query_operation` reconciles an interrupted build.

The game converts the blueprint into blocks, tiles, entities, navmesh updates, animations, and save data. Large builds should be a durable workflow with bounded batches and progress events, not thousands of unconstrained tool calls.

This supports both declarative blueprint construction and stepwise plans. The model chooses intent and parameters; ordinary game code performs collision checks, resource accounting, placement, pathfinding, animations, and rollback. No special embodied-agent subsystem is required.

## Dynamic quests, items, and rules

Separate semantic generation from executable mechanics. Let the model choose from or compose game-owned primitive IDs, validate the resulting JSON, and compile it into normal game data. Never execute model-authored source code.

A deterministic workflow is usually better for multi-stage generation: draft, validate references, calculate balance, request repair if needed, import assets, then commit. `IGameMediaGenerator` can create optional visual or audio resources while the game validates type, size, storage path, ownership, and content policy.

## AI director or game master

Supply high-level world metrics, player history, pacing targets, and a bounded event catalog. Tools schedule encounters, reveal authored content, or propose new content. The game checks cooldowns, fairness, reachability, and difficulty before committing.

Use a separate director actor rather than mixing director privileges into every NPC. Scope tools and memory to the minimum authority each actor needs.

For a very large event or command catalog, expose `ToolCatalogExtension` instead of placing every schema in every request. For external catalogs, the default on-demand connector lets the model search, inspect, and then call a selected tool. `ToolPolicyExtension` remains the authorization layer regardless of how a tool was discovered.

## Learned runtime AI

Reinforcement-learning controllers, motion matching, perception networks, and low-level bots are outside the language-agent loop. They can coexist with it: learned systems produce observations or execute a high-level tool, while OpenGameAgent handles language, semantic planning, memory, and tool orchestration.

## Save and replay

Use stable session, actor, input, operation, and timeline IDs. After loading a save fork that can coexist with its source, assign both a new session/save namespace and a new timeline ID. Persist game state and OpenGameAgent stores in the same save transaction when possible. If that is impossible, reconcile pending action journal entries before accepting new inputs.

Never use wall-clock timestamps to decide whether an in-world memory happened before the current save state.
