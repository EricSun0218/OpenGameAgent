# Game integration patterns

The framework stays generic by standardizing agent execution, not gameplay. This page shows how common AI-game features map onto existing game systems.

## Conversational NPC

Input includes the player's utterance plus non-language interaction state. Context includes identity, visible scene, relationships, recent events, and recalled memory. Read-only tools can inspect additional detail; mutation tools can accept gifts, start quests, or alter disposition through game rules.

Route ambient conversation to `QuickResponse`. Route a conversation with available actions to `Agent`. Store only durable facts or relationship changes as long-term memories; a full transcript is not automatically good memory.

Canonical engine identifiers do not have to enter the model prompt. By default, the input envelope still exposes `ActorId`, `TimelineId`, `Tick`, and `Calendar` for compatibility. A host that treats those coordinates as private can suppress them or replace them with stable model-only aliases:

```csharp
var options = new GameAgentRuntimeOptions(provider, model)
{
    InputModelProjection = input => new GameInputModelProjection(
        actorId: opaqueAliasFor(input.ActorId),
        moment: null), // omit timeline, tick, and calendar
};

// To omit both actor and moment coordinates:
options.InputModelProjection = _ => GameInputModelProjection.SuppressCoordinates;
```

This projection changes only the model-visible JSON envelope and its `game.actor_id`, `game.timeline_id`, and `game.tick` message metadata. `GameSessionKey`, scheduler ownership, extension state, memory scope, tool-provider input, durable action intents, receipts, and `LastMoment` continue to use the canonical `GameInput`. Do not use a display name as an authority key. Keep the selector deterministic for a retried input: a durable tool-turn checkpoint fails closed if the resubmitted model-visible message does not match. Enabling projection affects new input messages; start a new session or explicitly migrate old transcript data if historical canonical envelopes must no longer be retained.

## Autonomous companion

The game emits an observation when goals, threats, resources, or player orders change. Tools expose high-level capabilities such as navigate, gather, defend, revive, or build. Low-level movement and combat remain deterministic game AI or learned control code.

Use steering to inject urgent state changes during a long run. A steering message should identify the new observation version so the model can abandon a stale plan. Use conflict keys to prevent two simultaneous writes to the same companion or resource.

For visual worlds, combine structured local state with a sparse BEV or topological map, then attach a screenshot or crop only when appearance or geometry matters. Do not serialize every voxel or pixel. Let the model select intent and targets, use read-only tools for exact follow-up queries, and leave pathfinding, placement, physics, and animation to deterministic game code. See [Image input and game perception](image-input.md).

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

Use `GoalLoopExtension` when an actor owns semantic goals that can wait for a tick or event and continue later. `GoalLoopOptions.MaximumActiveGoals` bounds active and waiting work, while `MaximumRetainedTerminalGoals` independently retains only the most recent completed, failed, or cancelled records for audit. Terminal retention never removes active or waiting goals, so long-running sessions do not exhaust their future goal capacity.

Use `AgentDelegationExtension` when one actor needs bounded background research or planning without sharing its mutable transcript. Official stores persist the immutable delegate request, lineage, inherited host execution scope, attempt count, and a renewable execution lease. After rebuilding the runtime, call `ResumePendingAsync`; it reclaims pending work and expired running leases with revision CAS, while a still-valid lease and the process-local active registry prevent duplicate execution. Runtime shutdown deliberately leaves an interrupted running record recoverable instead of falsely recording cancellation. Model-facing status and list tools never expose the persisted parent transcript or lease token.

```csharp
var delegations = new AgentDelegationExtension(
    executor,
    new FileGameAgentDelegationStore(saveDirectory));
await using var runtime = new GameAgentBuilder(provider, model)
    .UseExtension(delegations)
    .Build();

var resumed = await delegations.ResumePendingAsync(maximum: 128, cancellationToken);
var lineage = await delegations.ListAsync(
    new GameSessionKey(sessionId, actorId),
    rootDelegationId,
    maximum: 128,
    cancellationToken);
```

`parentDelegationId` creates a continuation/child record and preserves the original `RootDelegationId`; `list_delegations` returns a bounded owner-scoped lineage. The delegate tool provider receives the exact parent `GameExecutionScope`, so it can only narrow inherited authority. It must never grant a child tools or host capabilities that the parent scope did not allow. Every claimed request also carries a process-local `LeaseValidator`. The official local executor runs it at the final authorization boundary before every tool call, so a reclaimed stale worker may finish model inference but cannot touch a tool. Custom executors must enforce the same callback at their execution boundary. File stores provide local crash recovery and cross-process file coordination, not distributed scheduling; multi-host services should implement the same store contract over transactional shared storage.

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

Use `TaskPlanExtension` for an ordered checklist that must survive later inputs. It is separate from `GoalLoopExtension`: goals describe durable intent and game-time waits, while a task plan records an ordered execution path. An active or paused plan always retains one `InProgress` step, a completed prefix, and a pending suffix. The model cannot advance a step merely by claiming success; the host-supplied `GameTaskPlanEvidenceValidator` must accept the evidence against the current input, plan, and step.

When the host can prove that one committed input produced exactly one authoritative action receipt for exactly one active plan, it can keep mechanical evidence bookkeeping out of the model loop. Set `TaskPlanOptions.AllowModelAdvancement = false` to remove `advance` from the model-visible `manage_task_plan` schema, then call `TaskPlanExtension.AdvanceAsync` after the matching input is durably committed. The API reuses the same evidence validator, plan revision, once-per-input guard, terminal retention, and session-store compare-and-swap as the model tool. It rejects pending or unknown inputs and never requires the host to parse extension-owned JSON. Leave model advancement enabled when selecting a plan or evidence receipt is itself a real high-level choice.

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

`advance` requires the plan revision and accepted evidence and can succeed only once per input. `replace_remaining` preserves completed steps and replaces only unfinished work. `pause` changes `Active` to `Paused` without changing any step, and `resume` restores that same plan to `Active`; both require `expectedRevision`. A repeated pause of an already paused plan, or resume of an already active plan, is an idempotent success only when the supplied revision still matches: it does not write state, increment the revision, or emit another change event. A stale revision is always a conflict. `fail` and `cancel` remain terminal and terminal plans cannot resume.

Paused plans remain visible through `list_task_plans` and `TaskPlanExtension.ReadAsync` without requesting terminal records. They continue to count toward `MaximumActivePlans`, but do not contribute pending work. While paused, every mutation except idempotent `pause` and `resume` is rejected; the checklist must resume before it can advance, replan, fail, or cancel. A successful transition increments the plan revision, persists the current game moment, and publishes `GameTaskPlanChanged` with reason `pause` or `resume`; as with every extension change event, wait for the matching `SessionSaved` event before treating it as committed UI state. State is namespaced by the runtime's session/actor key and persists through any `IGameSessionStore`.

The tool payload cannot select an owner, session, or actor scope. Plans always use the already-authorized `GameInput`/`GameSessionKey`; a server host must resolve and authorize that key before invoking the runtime.

The evidence validator is a read-only authority check, not another world mutation hook. Validate a receipt, observation revision, or game-owned fact there; perform actual state changes through ordinary authoritative tools and durable actions.

`PlanChanged` and `GoalChanged` carry the session/actor key and input ID. A UI that must show only committed state should buffer those channels and finalize them after the matching `SessionSaved` lifecycle event; a run that loses session CAS must not become authoritative UI state.

### Host query migration

Hosts that previously inspected `GameSessionSnapshot.ExtensionState` should migrate to `GoalLoopExtension.ReadAsync` and `TaskPlanExtension.ReadAsync`. Treat extension-state key encoding and JSON documents as private storage details. `GameGoalChanged` now follows `GameTaskPlanChanged`: its constructor and every published event include `GameSessionKey` and `InputId`, so event consumers should correlate the change with the matching saved input before updating authoritative UI.

Existing task-plan documents remain valid without migration. `Paused` was appended to the public status enum and is serialized by name; the numeric values and stored JSON names of `Active`, `Completed`, `Failed`, and `Cancelled` are unchanged. Hosts that switch exhaustively on plan status should add `Paused` as a visible, non-terminal, non-runnable state.

## Monthly or turn-based evolution

Represent the calendar in `GameMoment.CalendarJson` while using `Tick` for ordering. A monthly advance can be a named `DurableGameWorkflow`:

1. calculate deterministic production and upkeep;
2. identify exceptional factions or NPCs;
3. ask selected agents for decisions;
4. commit validated decisions through action handlers;
5. write memories and schedule the next occurrence.

Workflow checkpoints allow a wait between steps without losing progress. Use `agent.workflow_instance` metadata to resume the same instance intentionally.

When independent monthly branches may run together, use `DurableGameWorkflowGraph`. Dependencies are explicit, ready nodes run with bounded concurrency, joined outputs are presented in declaration order, and completed nodes are not rerun after a wait. A node that changes the world should use the durable action dispatcher with a stable operation ID because workflow and game-state storage are not automatically one transaction.

## Long-running world actions and narrated progress

Do not keep one model call or tool invocation open for an action that lasts several game hours, days, or turns. Split semantic intent from world execution:

1. the agent proposes or selects a bounded action through a typed tool;
2. the authoritative game validates it and durably commits either the action itself or a scheduled action record;
3. the receipt closes that exact mutation attempt;
4. `TaskPlanExtension` or `GoalLoopExtension` retains the actor's semantic objective;
5. game-time triggers enqueue bounded progress, interruption, success, or failure observations;
6. later agent runs may narrate progress, revise the remaining plan, or write a memory without replaying the original mutation.

Use stable action, schedule, mailbox, and input IDs. A progress observation is not another receipt for the original action and must not reuse its operation ID. If a progress update changes the world, expose that change as its own durable action with its own authority check and receipt.

This pattern supports journeys, construction, research, employment, trade routes, rescues, faction campaigns, and other multi-tick activities. The game owns simulation and completion conditions; the model handles semantic planning, explanation, negotiation, and adaptation. A save should persist the game action record together with the relevant workflow/plan checkpoint, scheduler state, and mailbox state, or reconcile them before admitting new work.

## Multi-stage dialogue and generated content

Use ordinary agent turns for open conversation and a fixed workflow when the product requires explicit stages such as inspect context, draft, validate references, calculate game values, repair invalid output, localize, and publish. Each stage receives bounded structured data. Only the final game-owned commit tool may create quests, items, rules, policies, rumors, histories, or ending records.

Different dialogue modes—negotiation, argument, friendship, recruitment, voting, trade, surrender, or advice—are route, prompt, context, tool, and policy compositions rather than separate runtime subsystems. Keep the actor identity, visible facts, allowed tools, and audience policy authoritative at every stage. A workflow may return structured interaction choices for the UI, but those choices do not gain permission merely because the model generated them.

## Generated plans and behavior assets

An agent may draft a goal graph, utility plan, behavior tree, schedule, quest graph, policy, or another game-native asset. Keep that asset format in game code instead of making it a runtime schema. A safe pipeline is:

1. expose the allowed node/action catalog as bounded structured context or a searchable read-only tool;
2. request a closed structured draft rather than executable code;
3. compile and validate references, cycles, depth, costs, permissions, and game-specific invariants in deterministic code;
4. return bounded validation diagnostics for repair;
5. publish the accepted asset through a durable game action;
6. let the authoritative simulation execute it and feed observations back to later agent turns.

The generated asset can persist and run without another model call. Model output never becomes a new tool or permission by itself, and a generated node can invoke only actions that the game already registered and authorized. Use a fixed workflow when draft/validate/repair stages must be reproducible, and store the published asset as an artifact or game save record according to the game's ownership model.

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

When generated bytes must become a persistent game asset, use `GameGeneratedAssetPipeline` rather than treating a provider URL as a finished asset. The pipeline binds a stable operation to the session, actor, game moment, model, generator, importer, and request fingerprint; materializes bounded outputs into content-addressed storage; persists the manifest before import; and records the engine's authoritative receipt. `GameGeneratedAssetActionImporter` can route the final import through `DurableGameActionDispatcher`, so an interrupted import is recovered by operation ID instead of repeated blindly. The game still owns the asset schema, moderation, licensing, quotas, engine-thread scheduling, and final save mutation. See [Generated assets](generated-assets.md).

## AI director or game master

Supply high-level world metrics, player history, pacing targets, and a bounded event catalog. Tools schedule encounters, reveal authored content, or propose new content. The game checks cooldowns, fairness, reachability, and difficulty before committing.

Use a separate director actor rather than mixing director privileges into every NPC. Scope tools and memory to the minimum authority each actor needs.

For a very large event or command catalog, expose `ToolCatalogExtension` instead of placing every schema in every request. For external catalogs, the default on-demand connector lets the model search, inspect, and then call a selected tool. `ToolPolicyExtension` remains the authorization layer regardless of how a tool was discovered.

When a mode or player setting disables a tool, hide it during collection with
`RegisterToolVisibilityPolicy`. The policy is recomputed from the current `GameInput` for every model
request and sees each collected `ToolDefinition`, including tools supplied by another extension or by
the runtime host. Multiple policies form an allow-list intersection. This is distinct from
`ToolPolicyExtension`: visibility prevents the model from seeing or choosing a schema, while the
execution policy still validates any call that reaches the authority boundary. Use both for
world-changing operations.

For a high-risk call that requires player or operator consent, add `ToolApprovalExtension` after
the ordinary policy extension. It runs at a final, non-rewriting kernel boundary after argument
preparation, policy rewrites, schema validation, and conflict-key resolution. `disabled` and
`explicit-only` calls fail closed; `allowed-in-task` requires a host-attested task scope;
`confirm-once` creates a durable pending request. A grant is bound to session, actor, input, run,
tool call, canonical argument digest, timeline, save generation, and world revision, and is consumed
exactly once before the executor is entered. A changed argument or loaded/advanced world invalidates
the grant. See [High-risk tool approval](tool-approvals.md).

## Learned runtime AI

Reinforcement-learning controllers, motion matching, perception networks, and low-level bots are outside the language-agent loop. They can coexist with it: learned systems produce observations or execute a high-level tool, while OpenGameAgent handles language, semantic planning, memory, and tool orchestration.

## Bounded NPC adaptation

Long-lived NPCs can improve without allowing a model to rewrite code or game rules. Keep two kinds of adaptation separate:

- experiences, relationships, preferences, and world facts belong in scoped `GameMemory` records;
- reusable procedures belong in versioned `GameSkill` instructions selected for that actor's input and available tools.

`BehaviorLearningExtension` implements this pattern as an optional official extension:

1. the NPC records a bounded structured reflection: observation, strategy, authoritative outcome, applicability, and known failure modes;
2. it proposes a behavior Skill revision while ordinary facts remain in `GameMemory`;
3. the proposal cites game-owned evidence such as input IDs, committed action operation IDs, receipts, or offline evaluation findings;
4. a host validator checks that evidence and verifies the proposal cannot add tools, permissions, executable code, credentials, or hidden world data;
5. the host-selected policy either holds the validated immutable version for explicit activation or activates it immediately;
6. a composite Skill may contain ordered steps such as `collect_resource -> construct_structure -> install_light`, but each step can only reference a declared tool and is executed by the normal Agent loop;
7. older versions remain available for audit and rollback, while rejected and obsolete proposals have bounded retention;
8. later traces and evaluations can demote or roll back a version that performs worse.

The model may call `propose_behavior_learning` only when the trusted execution scope grants `GameExecutionCapabilities.BehaviorLearning` and the extension's optional in-run policy opts that input in. The default adds no proposal tool to normal NPC runs; an isolated reviewer can submit a typed candidate with `ProposeAsync`. `BehaviorLearningOptions.Mode` can disable the feature, retain validated candidates for explicit review (the default), or auto-activate a candidate after the host validator accepts it. In review mode, the host reads candidates with `BehaviorLearningExtension.ReadAsync` and activates one exact version with `ActivateAsync`, binding the first activation to the current timeline, world generation, world revision, and session CAS revision. `RecordEvaluationAsync` tracks outcomes and demotes a version after the configured consecutive-failure threshold; `DemoteAsync`, `RejectAsync`, and reactivating an older version provide explicit recovery paths.

The model may propose; the host decides what becomes active. Active versions are projected through the normal dynamic skill provider, so their declared tools must already be present for the current input. Composite steps do not execute behind the runtime: every call still passes normal tool visibility, policy, approval, schema validation, conflict coordination, durable action dispatch, and game authority. A learned instruction cannot register a tool, expand authority, change game rules, execute code, expose credentials, or consume private reasoning. World-generation-scoped versions disappear from model context after a load boundary changes. Actor-wide versions are disabled by default and require an explicit option. Rejected, superseded, and demoted audit versions have bounded retention; active and pending proposals are never pruned by retention cleanup.

Individual learning remains scoped to `(sessionId, actorId)`. If a host wants reusable common procedures, it may install `SharedBehaviorCatalogExtension`, publish one validated immutable version for a game/world/role/faction audience, and let each eligible NPC explicitly adopt it. Publishing only makes the behavior discoverable; it never pushes it into every NPC. The host assigns a catalog-wide behavior family and family version independently from the source NPC's local version. Adoption is guarded by the current world boundary, audience membership, a per-actor validator, and session CAS. Failures suspend only that actor's adoption, while host revocation removes the publication from future runs and skill selections. Active/suspended adoption capacity, inactive audit retention, returned discovery count, and paged discovery scan work are bounded independently. See [NPC behavior learning and self-evolution](behavior-learning.md) for the complete contract and examples.

## Save and replay

Use stable session, actor, input, operation, and timeline IDs. After loading a save fork that can coexist with its source, assign both a new session/save namespace and a new timeline ID. Persist game state and OpenGameAgent stores in the same save transaction when possible. If that is impossible, reconcile pending action journal entries before accepting new inputs.

Never use wall-clock timestamps to decide whether an in-world memory happened before the current save state.

## Reopening a persisted conversation

Use `GameAgentRuntime.ReadTranscriptAsync` in-process, or the authorized `POST /v1/transcript` endpoint through `ServerGameAgentClient.ReadTranscriptAsync`, to rebuild a chat window for one `(sessionId, actorId)`. Pages contain the current durable transcript in stable chronological order. The page size is limited to 256 messages and the opaque cursor is bound to the session revision; if another run or a save rollback changes that revision, the old cursor fails with `transcript_changed` instead of combining two histories.

This is the active model transcript, not an append-only audit log. Compacted messages are represented by their durable summary, and a rollback exposes the restored transcript. Products that require immutable audit history should record it through their own host-owned event or trace storage instead of duplicating the runtime transcript.

`ImageAttachmentContent` is returned as attachment metadata only. Fetch bytes separately through the authorized attachment endpoint when the UI actually needs them. Server hosts must authorize `GameAgentServerOperation.ReadTranscript` before touching the runtime or session store, and should install an audience policy whenever different viewers can see different messages. Owner/public projections remove reasoning, signatures, private messages, and tool details; provider credentials never enter the transcript response.
