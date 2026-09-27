# @opengameagent/extensions

Optional game-native extensions around the single Pi-backed Agent loop. A host may compose these capabilities without
changing the loop or granting them authority over game state.

Goals and ordered plans persist explicit host-bounded state, use optimistic revisions, keep game time exact, validate
completion evidence through a host adapter, and support pause/resume plus bounded terminal purge.
The optional `goal_create` Tool forwards its current run cancellation signal, and `GoalService.create` checks that exact
signal immediately before the Goal authority write so an already-revoked execution cannot create a new Goal.

The host supplies one complete `GoalLimits` object when it constructs the `GoalStore`; `GoalService` uses that same
immutable configuration. `maxPlanBytes` bounds the canonical UTF-8 JSON for the complete ordered plan,
`maxRecordBytes` bounds the complete canonical host record projection (with the bigint cursor represented as its exact
decimal string), and `maxModelResultBytes` bounds the canonical JSON text returned by goal Tools. These aggregate limits
are mandatory in addition to the existing per-field and count limits; the framework does not derive defaults.

Create, replan, model update, and host receipt advancement check the applicable aggregate result before committing a
state transition. Stores also check the full record before a write becomes durable. SQLite writes canonical plan JSON,
rejects an oversized raw persisted plan before parsing it, and reapplies plan and record limits during hydration. A
lower recovery budget therefore fails closed instead of returning a partial or oversized goal.

When a committed game input already has an authoritative receipt, configure `GoalService` with a committed-input
verifier and call `advanceFromHost`. The request binds the exact owner, timeline, rewind generation, optional session and
actor, goal revision, current step, committed input, evidence reference, and game time. The service verifies the input
and evidence, completes only the current `in_progress` step, starts the next ordered step or marks the goal achieved, and
persists the change through the existing store compare-and-swap. Only an exact replay of the same partition, input,
original revision, step, evidence, game time, and record time is idempotent without re-running external verifiers;
stale or competing requests cannot acquire the committed result.

For a durable Action-owned write, `@opengameagent/actions` can retain that exact Goal request as a bounded settlement
binding and recover it through `ActionGoalProjector`. The game still supplies the Action receipt verifier, evidence
reference, and game time; `GoalService` remains the Goal authority and can be passed directly as the projector's
host-advance implementation. A stop after Goal compare-and-swap is safe because replay returns `already_advanced` only
for the exact prior host request. A rejected terminal Action is durably settled as `source_failed` without calling the
Goal authority; unverifiable successful receipts remain unresolved rather than being guessed away. Goal metadata stays
outside native Action payloads and model-visible Tool results.

Set `includeModelUpdateTool: false` when creating goal Tools to omit `goal_update` from the Pi-visible Tool set. The game
can then own mechanical receipt advancement without adding a workflow route or requiring the model to restate a result.
Hosts may still call `GoalService.update` directly for authorized pause, resume, replan, failure, or cancellation policy.
The framework does not interpret action payloads or outcomes: the configured evidence verifier must resolve succeeded,
failed, and unknown receipts against game-authoritative state.

Game signals are immutable, idempotent facts recorded by the authoritative host. Durable waits can match a signal that
arrives before or after wait registration, constrain matching by exact rewind generation and optional actor/session,
and bind a match to a goal step or host correlation. Recording and matching are atomic; cancellation uses an expected
revision; expiration advances only from a host-supplied game tick. A host must provide every payload, selector, fan-out,
page, expiration, and purge limit explicitly.

Recording is idempotent. Its result contains the current bounded snapshot of matched wait IDs, including waits registered
after the first record call; consumers use wait IDs and revisions as their idempotency boundary.

Game-time schedules are one-shot or fixed-interval declarations keyed to an exact timeline and rewind generation. The
game advances each schedule clock explicitly with a stable advance ID; no wall clock, background timer, or hidden
catch-up policy can move it. An advance must continue from the last accepted tick. If it skips an already-due occurrence,
exceeds its occurrence bound, or would overflow the pending outbox, the entire advance rolls back.

Due occurrences are materialized in chronological order into a durable outbox before delivery. Workers claim bounded
batches with expiring leases and may project an occurrence through `ScheduledSignalDispatcher` as an idempotent game
signal. A crash between signal recording and outbox completion is safe to retry because every occurrence keeps one
stable signal ID. Cancelling a schedule prevents future materialization but does not discard occurrences already in the
outbox. Terminal occurrences (`completed` or `cancelled`), terminal schedules, and advance identities have separate explicit purge operations; an
advance replay returns its retained occurrence records and fails if the caller's current result bound is smaller.

Actor mailboxes retain immutable, idempotent game inputs for one exact owner, timeline generation, session, and actor.
Each recipient is strict FIFO: an active head lease blocks later claims, and a claimed later message cannot pass the Tool
execution fence or complete before every earlier pending message settles. Claims, renewals, completion, abandonment, and
cancellation all use host-supplied canonical operational time. Lease expiry itself revokes authority, even before another
worker reclaims the message. Every capacity, payload, identifier, counter, page, claim, purge, and active-delivery bound is
explicit host configuration.

`MailboxService.list` is a read-only discovery surface. It always requires owner, timeline, and generation, and can
independently narrow results by session, actor, or status. Pages follow the global message-key order with an exclusive
cursor and return each record's exact recipient. Discovery never claims a message, creates a lease, increments an attempt,
or changes a revision; delivery still requires an exact-recipient `claim` and preserves that recipient's FIFO authority.

`MailboxRunLeaseRegistry` projects a claimed message into an ordinary Pi-backed game input without exposing its lease
token. The input identifier is stable for one delivery attempt and changes after reclaim, allowing the runtime's durable
input replay to remain safe within an attempt. Before submitting the run, persist
`mailboxDeliveryToRunLeaseReceipt(delivery, limits)`. The JSON-safe receipt is the durable handle accepted by `renew` and
`settle`, including after process restart without an in-memory registration. Completed settlement is idempotent for the
exact receipt even if the durable write succeeded before its response was lost. Failed or aborted settlement releases the
message to pending; replay confirms that release only while the same attempt remains current and fails closed after a new
attempt is claimed. `restore` has the narrower job of verifying a still-live receipt and reinstating the Tool-execution
fence before a resumed run may execute tools.

The host must either choose an initial lease horizon covering its complete run, abort, terminal-observation, shutdown,
restart, and settlement budget, or renew the receipt while its lease remains live. The framework does not invent that
product-specific budget or run a hidden renewal timer. The receipt contains the lease secret: keep it in host-owned
protected state, never put it in model input or game-visible output, and delete it after settlement.

Schedule and Mailbox services and stores expose `beginAuthorityRetirement(request)`,
`finalizeAuthorityRetirement(request)`, and `readAuthorityRetirement(partition)`. The request is
`{ retirementId, partition: { ownerId, timelineId, generation, sessionId, actorId }, recordedAt }`; every partition field
is mandatory. A stable retirement ID and exact request replay their current result; conflicting IDs or metadata reject.
Begin durably fences that exact lane, cancels future active schedules and unleased pending work, and preserves leased
pending work even if its lease has expired. Those retained leases immediately lose claim, restore, authorization,
renewal, completion and abandonment rights. Other lanes, including schedules without session/actor, remain independent.

Only call finalize after all matching producers, dispatchers, handoffs and run handlers have stopped executing.
For a shared SQLite Runtime, prefer `closeAuthority({ ..., workRetirement: request })` with the explicit
`drainWorkAuthority` host callback; it orders runtime drain and detached host drain before final lease release.
Finalize cancels remaining pending work and clears leases; it never claims business completion. Existing completed
occurrences and exact completed Mailbox receipt replays keep their original meaning. Fenced or finalized Mailbox
settlement otherwise throws `WorkAuthorityRetiredError`; once the durable result is `finalized`, the host can delete
its retained receipt and call `MailboxRunLeaseRegistry.release(input)` to remove local registration. Do not use a
missing purged message or an expired lease as proof that execution has drained.

The records expose `phase: "fenced" | "finalized"` and cumulative cancellation counts. Retirement records survive
`purgeTerminalOccurrences`, `purgeTerminalSchedules`, and Mailbox `purgeTerminal`, so an old lane cannot be recreated
after history cleanup. Each standalone store is atomic within its own authority. Composite atomic retirement across
stores is provided by the shared SQLite event-store binding; independent memory stores cannot provide that guarantee.

`GameInteractionService` is an optional player-question seam, not a UI or another conversation runtime. Share one service
across the actors that use the same game-owned broker, then expose its sequential Tool through the Agent's per-request
Tool source. Each request is bound to the exact input, Pi run/turn and Tool call; a stable SHA-256 request ID lets the
broker deduplicate transport retries, while a separate content fingerprint rejects conflicting concurrent reuse.

Questions may offer stable-ID choices, free text, or both. Multi-select is explicit. At most one choice may be marked as
recommended, and that marker is presentation guidance only: it never grants approval or game-write authority. A broker
must either answer every question or cancel the whole request. Requests, host answers and model-visible results are
strict JSON, are bounded by explicit host UTF-8/count/canonical-byte limits, and reject unknown, duplicated or ambiguous
answers. Pending requests have an explicit shared capacity. There is no framework timeout; the owning run's abort signal
always stops waiting even if a faulty UI promise does not settle, while games remain responsible for their own deadline
and accessibility/controller policy.

Behavior evolution is an optional proposal and registry seam around the same Pi loop. A model may submit a structured
reflection summary with host-verifiable evidence and an ordered Skill made only from tools visible in the current game
scope. The proposal captures an exact fingerprint of every referenced Tool descriptor and JSON Schema. Validation sees
descriptors rather than executable functions, and activation fails closed if a Tool disappears or changes.

`off`, `conservative`, and `aggressive` are host policies. Conservative mode requires an explicit host activation after
evidence and sandbox validation. Aggressive mode may activate immediately after the same validations; it does not permit
generated executable code, new tools, additional permissions, or automatic shared publication. Actor and host-selected
shared registries are isolated by owner, timeline, and rewind generation. Actor scopes take precedence when the host
requests them before shared scopes. Versions are immutable, active pointers use optimistic revisions, and rollback or
demotion records a registry change. The Pi context adapter projects only active instructions, not reflection evidence or
private validation material, and ordinary Pi Tool calls continue to pass through the game's normal execution guards.
Proposal decisions, activations, rollbacks, and demotions reject regressing game or canonical wall time. Hosts bound open
proposals and retained versions explicitly. Pending and validated proposals, active versions, and provenance required by
retained versions cannot be purged; inactive versions, detached terminal proposals, and registry changes have separate
explicit bounded cleanup operations.

These services do not add an execution router or a second workflow engine. Hosts decide whether to expose any operation
as a Pi tool, wake a passive actor from a matched correlation, or project bounded extension state through an Agent
context source. The game remains authoritative for time and signal writes.
