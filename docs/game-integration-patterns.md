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

When an AI budget ends exactly at a game-time boundary, inspect mailbox backlog without claiming work or invoking a model:

```csharp
var recipients = activeActors
    .Select(actorId => new GameMailboxRecipientKey(sessionId, actorId))
    .ToArray();
var pending = await mailbox.GetPendingStatusAsync(
    recipients,
    DateTimeOffset.UtcNow,
    cancellationToken);

var mustPauseAtBoundary = pending.Any(status => status.IncompleteCount > 0);
var canRunImmediately = pending.Any(status => status.ReadyCount > 0);
```

`GetPendingStatusAsync` is a typed, read-only snapshot. It returns one result per requested key in input order, including zero counts for missing mailboxes, and never returns message payloads. `ReadyCount` includes unleased messages and messages whose operational lease has expired; `LeasedCount` contains incomplete messages whose operational lease is still active; `IncompleteCount` is their sum. Querying does not acquire a lease, increment `Attempt`, complete or abandon a message, or call the model. The built-in file store evaluates the whole recipient batch in one directory pass rather than scanning all mailbox files once per NPC. Supply the same trusted operational clock used for `ClaimAsync`. A concurrent claim or settlement may make any snapshot stale, so use it for scheduling and causal-boundary admission, not as authority to complete a specific message.

Use `GoalLoopExtension` when an actor owns semantic goals that can wait for a tick or event and continue later. `GoalLoopOptions.MaximumActiveGoals` bounds active and waiting work, while `MaximumRetainedTerminalGoals` independently retains only the most recent completed, failed, or cancelled records for audit. Terminal retention never removes active or waiting goals, so long-running sessions do not exhaust their future goal capacity. Use `AgentDelegationExtension` when one actor needs bounded background research or planning without sharing its mutable transcript. Delegates still receive explicitly scoped context and tools; delegation is not permission escalation. Delegation status can be persisted, but the included local executor runs child work in the current process and does not automatically resume an in-flight child after a process restart. Use a host-owned durable workflow or executor when child execution itself must survive restarts.

The host can project goals and task plans after loading a save without invoking a model and without parsing extension-owned JSON keys:

```csharp
var authorizedSession = new GameSessionKey(sessionId, actorId);
var goals = await GoalLoopExtension.ReadAsync(
    sessionStore,
    authorizedSession,
    includeTerminal: true,
    cancellationToken);
var taskPlans = await TaskPlanExtension.ReadAsync(
    sessionStore,
    authorizedSession,
    cancellationToken: cancellationToken);

ui.Render(goals.SessionRevision, goals.Goals, taskPlans.Plans);
```

These readers are read-only projections over `IGameSessionStore`. They do not run routing, providers, tools, pruning, or other extension lifecycle work. A missing session returns revision `0` and an empty collection. The caller must authorize the `GameSessionKey` before querying it; the readers deliberately do not replace host ownership policy.

Use `TaskPlanExtension` for an ordered checklist that must survive later inputs. It is separate from `GoalLoopExtension`: goals describe durable intent and game-time waits, while a task plan records an ordered execution path. An active plan always has one `InProgress` step, a completed prefix, and a pending suffix. The model cannot advance a step merely by claiming success; the host-supplied `GameTaskPlanEvidenceValidator` must accept the evidence against the current input, plan, and step.

```csharp
var plans = new TaskPlanExtension(
    async (request, cancellationToken) =>
        await receipts.ExistsAsync(
            request.Input.SessionId,
            request.Input.ActorId,
            request.Reference,
            cancellationToken),
    new TaskPlanOptions
    {
        MaximumActivePlans = 8,
        MaximumRetainedTerminalPlans = 32,
    });

var runtime = new GameAgentBuilder(provider, model)
    .UseSessionStore(sessionStore)
    .UseExtension(plans)
    .UseExtension("plan-ui", "1", api =>
        api.Subscribe(TaskPlanExtension.PlanChanged, (change, _) =>
        {
            ui.Enqueue(change.Session, change.Plan);
            return ValueTask.CompletedTask;
        }))
    .Build();
```

`advance` requires the plan revision and accepted evidence and can succeed only once per input. `replace_remaining` preserves completed steps and replaces only unfinished work. `fail` and `cancel` are terminal. Active plans contribute pending work to routing; terminal retention is independently bounded and never consumes active-plan capacity. State is namespaced by the runtime's session/actor key and persists through any `IGameSessionStore`.

The tool payload cannot select an owner, session, or actor scope. Plans always use the already-authorized `GameInput`/`GameSessionKey`; a server host must resolve and authorize that key before invoking the runtime.

The evidence validator is a read-only authority check, not another world mutation hook. Validate a receipt, observation revision, or game-owned fact there; perform actual state changes through ordinary authoritative tools and durable actions.

`PlanChanged` and `GoalChanged` carry the session/actor key and input ID. A UI that must show only committed state should buffer those channels and finalize them after the matching `SessionSaved` lifecycle event; a run that loses session CAS must not become authoritative UI state.

### Host query migration

Hosts that previously inspected `GameSessionSnapshot.ExtensionState` should migrate to `GoalLoopExtension.ReadAsync` and `TaskPlanExtension.ReadAsync`. Treat extension-state key encoding and JSON documents as private storage details. `GameGoalChanged` now follows `GameTaskPlanChanged`: its constructor and every published event include `GameSessionKey` and `InputId`, so event consumers should correlate the change with the matching saved input before updating authoritative UI.

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
