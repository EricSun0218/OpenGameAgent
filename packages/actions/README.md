# `@opengameagent/actions`

Durable, content-bound action intents, claims, receipts, conflict barriers, and authoritative reconciliation.

The game remains the authority for every write and reconciliation result.

`createReliableActionTranscriptRecovery` is a read-only bridge for durable Tool restart. When a Tool-start record carries
the package-owned `act_` operation ID, the bridge queries the action journal: a missing intent is reported as
`not_started`, a succeeded or failed receipt is projected through the normal model-safe result, and pending, claimed, or
uncertain state is reported as `outcome_unknown`. Other operation namespaces are left to another recovery adapter. Before
accepting a receipt, the bridge verifies the action name, coordinate, and canonical payload fingerprint against the exact
Tool-start arguments. It never submits, claims, retries, or replays an action.

`createReliableActionTool` implements the Agent durable-execution hook directly. Its synchronous operation-key callback
must be pure: the returned action identity and the later submitted intent use the same captured key, while game authority
and the complete intent are read only after the Agent's final policy and execution-guard checks.
The factory always declares `executionMode: "sequential"`; a reliable Action is an exclusive durable Tool and cannot opt
into Pi's parallel Tool path.
Caller cancellation is rechecked after every asynchronous admission boundary. Cancellation before admission creates no
Action; cancellation after submit or approved admission stops only the current wait and never rolls back the durable
record. The Agent leaves that started Tool call open for authoritative transcript recovery. A settlement bridge must
return the same operation ID and intent fingerprint that it received before any terminal receipt is projected.
Once approved admission or submit is invoked, a non-cancellation bridge failure or an inconsistent later result is
reported as an unresolved durable execution so the Agent recovers from the action journal. An authoritative admission
result with no Action remains an ordinary policy denial.

`ActionCoordinator.waitForSettlement` and `ActionApprovalCoordinator.waitForDecision` centralize abort-aware durable
waiting for local hosts. Each wait requires an explicit positive polling interval within Node's supported timer range and
re-reads the canonical journal/store, so a state transition committed through another SQLite authority is still observed.
A missing exact record fails closed. Approval waits wake no later than the request's own `expiresAt`; one revision-CAS
transition materializes `expired` at that exact timestamp, while a concurrent player decision can still win before it.
Caller cancellation stops only the wait and never rolls back an Action or changes a still-pending approval.

An Action Tool may provide `resolveGoalProjection` to attach one host-owned Goal settlement binding before Action
admission. The binding identifies the exact Goal partition, step, expected Goal revision, and committed input ID; it is
normalized into the content fingerprint and stored with the Action rather than added to the game-native payload or the
model-visible result. Both direct and approved admission use the same prepared intent. Hosts that enable this seam must
configure all four positive `goalProjection` limits on the Action journal: serialized binding bytes, unresolved bindings
per actor stream, recovery page size, and complete serialized Action-record bytes. The record budget is enforced before
every Goal-bound write and again before recovery hydration. Every unresolved record reserves the larger
`source_failed` settlement shape, so a receipt that fits only before its mandatory settlement cannot become a durable
poison record.
`SqliteApprovedActionAdmission` must receive the same limits so approval consumption and capacity-checked Action
admission remain one transaction.

`ActionGoalProjector` performs bounded recovery; it is not another Agent loop or workflow engine. It reads unresolved
bindings from the journal and calls the host verifier only for an Action with a terminal succeeded receipt. The verifier
owns the game-specific proof that the input committed, the receipt and post-write authority are exact, and the supplied
evidence reference and game time are authoritative. Failed, uncertain, nonterminal, rejected, malformed, or mismatched
facts do not advance a Goal. After verification the projector invokes the configured Goal host-advance seam with the
durable Goal identity. It records projection completion only after `advanced` or the exact idempotent
`already_advanced` result. A terminal failed Action with its rejected receipt, or a never-claimed Action failed after
its run authority closed, is durably settled as `source_failed` and
releases projection capacity without invoking either host collaborator. Pending and uncertain Actions, as well as a
succeeded receipt rejected by the verifier or Goal authority, remain unresolved for later authoritative recovery.

The binding remains unresolved if the process stops after the Action receipt, after verification, or after Goal
compare-and-swap but before Action projection completion. A later bounded scan retries the same immutable request; a
Goal implementation such as `GoalService.advanceFromHost` converges that replay through its existing fingerprint and
revision CAS. Cancellation stops the current scan and does not erase either durable authority. Transcript Tool recovery
continues to return the terminal model-safe Action result independently of this host projection.

For `confirm-once` Tools, the optional approval `presentation` Adapter projects only the already validated Tool
parameters into host-owned review data. Its callback receives no provider request or reasoning state. The Adapter must
supply positive `maxBytes` and `maxDepth` limits; OGA has no presentation defaults. OGA detaches and validates the
returned JSON before creating a durable approval, then includes it in the request fingerprint and content-derived
approval ID. The exact projection is persisted with the request and returned by Runtime approval list and decision
responses, while the game remains responsible for wording and rendering. A limit, JSON, callback, or cancellation
failure before `ensure` creates neither an approval nor an Action.

Every approval request also stores the originating Action's complete `RunCoordinate` and binds it into the request
fingerprint. `ActionApprovalCoordinator.listPending` accepts an explicit request object and can select one exact `runId`;
the same optional selector on `ActionCoordinator.nextDelivery` limits pending dispatch, unresolved reconciliation, and
lease expiration work to that run. Omitting `runId` retains actor-scoped worker behavior.

Bind `new InMemoryActionJournal({ runAuthority: events })` to the same `InMemoryEventStore` used for run admission.
Its `assertRunAuthorityOpenSynchronously` check executes inside the journal's synchronous mutation after its mutex is
acquired. The binding must be synchronous; an asynchronous preflight cannot fence a later write. Omitting this binding
selects standalone, unscoped Action use. SQLite journals and `SqliteApprovedActionAdmission` install the shared run
authority tables and check them inside the same immediate transaction as new admission or dispatch. Scoped run mappings
and closure tombstones survive event and Action retention. Unscoped runs remain usable. Exact persisted Action replays
and already-consumed approval replays are reads; a missing Action is never recreated from a consumed approval.

Each `nextDelivery` first retires at most `candidateLimit` closed pending Actions for the actor, including old runs
outside its dispatch `runId` selector. This maintenance does not claim work. A never-claimed Action becomes `failed`
with attempt `0` and no invented game receipt; a retry proven `not-applied` retains its rejected receipt and becomes
`failed`. A Goal-bound failure still requires the bounded projector scan before terminal purge. Hosts keep polling to
drain larger closed batches. Closed runs cannot acquire a new claim or retry after reconciliation. Existing claimed or
uncertain effects remain recoverable, and their exact receipts are accepted after closure.

Expiry and pending candidate selection each inspect at most `candidateLimit` additional records. A safe expired claim
is replaced only when its next claim can be persisted atomically. Until then its original claim and conflict ownership
remain intact, and blocked redelivery returns a reconciliation delivery. An expired closed-run claim becomes uncertain
without dropping that fence. Persisted pending Actions must be either never claimed or backed by a rejected receipt for
their current attempt; an earlier rejection cannot certify the outcome of a later attempt.

The journal's `nextDelivery` returns `{ delivery?, changed }`, with `changed` describing only mutations committed by
that selection. `ActionCoordinator.nextDelivery` returns the delivery and wakes local waiters only when `changed` is
true. Empty scans and repeated reconciliation reads do not advance the notification revision. Hosts take
`changeRevision()` before reading and pass that snapshot to `waitForChange`; a notification during the read therefore
cannot be lost. Wait timeouts remain explicit, and writes made through another coordinator still require polling.

After a worker restart that retained only the actor stream identity, call `ActionCoordinator.recoverDelivery` before
resuming normal delivery. It returns only the oldest already `claimed` or `uncertain` Action as a `reconcile` delivery,
with the original claim token and attempt. An empty result never expires a claim, acquires pending work, increments an
attempt, or releases conflict ownership; continue with `nextDelivery` only after recovery is complete.

Retention is an explicit host maintenance operation, not a built-in TTL. `ActionCoordinator.purgeTerminal` removes at
most the requested batch of `succeeded` or `failed` records whose `updatedAt` is strictly before the supplied canonical
timestamp; a Goal-bound Action remains protected until its projection is durably settled. The equivalent Approval
operation removes only `rejected`, `consumed`, or `expired` records. It never removes `pending` or still-usable
`approved` authority. The host must pass every still-referenced Action or Approval identifier in `retainOperationIds` or
`retainApprovalIds`; those records are excluded even when otherwise eligible. Both operations are actor-stream scoped,
deterministic by update time and insertion order, and return the exact identifiers removed so the host can checkpoint
its maintenance work.

Every purge request also requires `retainBudget: { maxCount, maxBytes }`. These host-selected non-negative safe integers
bound the complete protection list before a snapshot, uniqueness set, or deletion is created. `maxBytes` counts the sum
of identifier UTF-8 bytes, excluding JSON punctuation. `{ maxCount: 0, maxBytes: 0 }` permits only an empty list.
Over-budget lists fail with `RangeError`; malformed budgets, duplicate IDs, or non-string entries fail with `TypeError`.
Never truncate a protection list to fit the budget: provision enough capacity for every live reference or postpone purge.
