# `@opengameagent/storage-sqlite`

Single-file SQLite authority, migrations, integrity checks, transactions, durable canonical events, and durable Agent transcripts.

The host must explicitly choose its lock wait and startup integrity-check policy. Feature packages share one
`SqliteAuthority`; they do not open or migrate independent databases.

The canonical event authority additionally requires `ownership: "exclusive"`. A file-backed authority acquires and
retains SQLite exclusive ownership before application identity, migration, integrity, or event recovery work begins.
Every runtime store then uses that one connection. A competing authority follows the host's existing `busyTimeoutMs`
policy and fails before it can expose `SqliteEventStore`; `close()` releases ownership, and an exited process loses its
SQLite lock without a lease, heartbeat, or stale-owner timeout. Omitting `ownership` keeps shared WAL access for
standalone stores whose own transaction, revision, or fenced-lease contracts support competing connections; a shared
authority cannot construct `SqliteEventStore`.

An abrupt owner-process exit preserves the last committed WAL prefix and releases the operating-system lock. Opening a
replacement authority performs SQLite recovery and full or quick integrity verification before the runtime can use the
stores. An event transaction interrupted by process termination is absent after recovery; a previously completed input
returns its durable terminal result, while an admitted nonterminal input is failed closed without invoking Pi again.
Hosts must keep a hot `-wal` file with its database under their original names until recovery. The `-shm` wal-index is
disposable and may not exist under exclusive locking, but no database, WAL, or SHM file may be copied, moved, deleted,
or restored independently while any authority connection remains open.

Optional goal, signal, game-time schedule, actor-mailbox, artifact, delegated-task, and behavior-evolution stores use that same authority for atomic
admission, rewind-generation isolation, bounded maintenance, restart-safe wait matching, and durable delivery. Schedule
clock advancement, occurrence materialization, and schedule state updates share one transaction. Mailbox claims preserve
a strict per-recipient FIFO prefix and fence expired or out-of-order workers across process restarts and competing
authority connections. Read-only mailbox discovery scans one owner/timeline generation in global message-key order and
can independently filter by session, actor, or status without acquiring delivery authority. Immutable artifacts atomically
enforce per-session-and-actor count/byte capacity, validate their content fingerprints on hydration, and reject conflicting
identity reuse. Delegated tasks use expiring fenced leases,
idempotent terminal settlement, strict record hydration, and the canonical event store's input identity to recover without
repeating an already completed child run. The in-memory stores in the feature packages are conformance fixtures; a
durable local or self-hosted runtime should use this shared SQLite layer.

For atomic work retirement, construct `SqliteEventStore(authority, { goalStore, scheduleStore, mailboxStore })` with
stores on that exact authority connection. `closeAuthority` commits its run fence, optional Goal retirement and both
work-lane fences together. A binding mismatch or retirement conflict rolls back the entire transaction.
After Runtime and the explicit host `drainWorkAuthority` callback finish, `finalizeAuthorityClosure` cancels retained
pending occurrences and messages, clears their leases and records the final result in one transaction. Calling that
low-level method directly asserts that every matching holder has already stopped executing. A partial finalization
failure rolls back both work stores and preserves `fenced`; it never reports partially released work as finalized.

Work retirement selects exactly `{ ownerId, timelineId, generation, sessionId, actorId }`, with all fields required.
Schedule advancement can still advance the shared owner/timeline clock for other lanes. Retired lease tokens remain
stored during the fenced phase but cannot authorize, renew, complete or abandon work. Permanent retirement records and
their accumulated cancellation counts survive terminal-history purges, preventing old-generation resurrection.

`SqliteGoalStore` receives the host's complete `GoalLimits` configuration. It persists canonical plan JSON, rejects raw
plan text above `maxPlanBytes` before JSON parsing, and validates the complete hydrated record against
`maxRecordBytes`. Create and replacement validation occurs inside the same immediate transaction, so an aggregate-limit
failure cannot leave a committed partial transition.

Action approvals persist an optional host-projected JSON presentation inside the same canonical approval record. The
projection is content-bound by the request fingerprint and survives restart with the policy, intent, authority,
decision, and one-time consumption state; SQLite does not derive, rewrite, or separately cache presentation data.
Action and approval indexes also persist the source run ID for exact-run delivery and discovery. An empty v2 Action
journal upgrades in place. A v2 journal containing Actions fails the v3 migration closed because those identities did
not bind the source coordinate and cannot be rewritten safely; the host must explicitly retire that pre-v3 authority
instead of allowing automatic deletion or effect re-execution. Legacy approval rows that predate coordinate binding
remain available for audit but are excluded from pending discovery and fail closed on direct use; the Tool must create
a new bounded request.
The Action recovery index selects the oldest claimed or uncertain actor-scoped record across runs. Its recovery query is
read-only, so reopening the authority and polling concurrently preserve the stored claim fence and pending queue exactly.
Terminal Action and Approval retention uses indexed, actor-scoped, cutoff-and-batch deletion in the same immediate
transaction. Host-retained identifiers are excluded; Actions with unresolved Goal projections, plus pending or approved
approvals, are never candidates.
Action, Approval, event, and transcript purge requests require `retainBudget: { maxCount, maxBytes }`. The shared protocol
validator checks the full original protection list's count and summed UTF-8 identifier bytes before copying, set creation,
SQL serialization, or a deletion transaction. The host chooses both non-negative safe integer budgets; zero permits an
empty list. Missing, malformed, duplicate, or oversized protection fails before mutation and never truncates references.

`SqliteActionJournal` also persists an optional Action-to-Goal settlement binding and a durable settlement outcome:
`advanced` or `source_failed`. Admission checks the host's per-stream unresolved-binding capacity in the same immediate
transaction that inserts the Action. Approved admission applies the same check before approval consumption commits, so
a conflict or capacity failure rolls back both changes. Every unresolved Goal-bound write reserves the larger terminal
settlement shape inside the byte budget before mutation. Recovery uses the stable Action insertion sequence, an explicit
page limit, and the same raw SQLite byte-length preflight before parsing a projected record through get, delivery,
approved admission, or projection enumeration. Settlement is fingerprint-bound and idempotent
only for the same outcome: `advanced` requires a succeeded receipt, while `source_failed` requires a rejected receipt
and releases capacity without Goal advancement. Reopening the shared authority preserves both unfinished and settled
bindings; the SQLite Adapter does not verify game receipts or advance Goals itself.

The transcript adapter appends versioned protocol records under an atomic expected-head revision and verifies stored
byte accounting, record fingerprints, retained-boundary metadata, and indexed mirrors before returning transcript
records. Message, Tool-start, Tool-result, repair, turn-closure, and context-checkpoint records use the same transactional
append path; a successful return is the durability boundary before an external Tool effect, replacement Pi history, or
later Pi request. Checkpoints initially retain the raw canonical prefix. An explicit bounded purge may later remove only
the prefix before an exact checkpoint's source closure while preserving the historical head and every protected
revision. Transcript record, commit, restore, and purge batch budgets remain explicit host inputs.
Restart repair verifies that the complete resulting prefix fits the restore budget before writing, then persists each
source-ordered repair record under its own expected-head append so a legal repair is not coupled to a multi-record commit
budget.

`SqliteEventStore` keeps each stream's revision head separately from retained event rows. Its retention transaction
deletes only a complete terminal run prefix selected by the host cutoff, batch size, and protected run IDs; reopening
preserves that head, monotonic append revisions, and explicit replay gaps even after all rows were removed.

The media-job Adapter stores each complete job as one canonical, fingerprinted snapshot. `create` rejects identity
reuse, and `modify` reads, validates, invokes the synchronous callback once, and replaces the snapshot in one immediate
transaction. Cold reads preflight the host's complete-snapshot byte budget before materializing the JSON and reapply the
same media limits used by the service. The selected media `adapterId` is part of that immutable snapshot identity; one
Store remains the authority for job IDs across every registered provider profile. On cold restart, the media service
resolves read, selection, reconcile, cancel, and restart only through this persisted ID, so a removed registry entry
fails closed rather than rerouting the job. SQLite does not persist provider objects or provider configuration.
SQLite's signed 64-bit revision ceiling fails explicitly; the Adapter does not retry, poll, split the media state
machine into child tables, or manage generated resource bytes.

`SqliteMemoryAuthority.readPartitionState(selector)` reads persisted lane counts and an opaque partition revision without
loading memory text, content, embeddings, or FTS rows. Remember, revise, and forget update metadata in the same transaction
as the authoritative record; idempotent remember and failed transactions preserve it. Reopening keeps counts and revisions
unchanged. The memory schema initializes this metadata once from existing records when installed, then updates only the
affected owner/timeline/generation and actor/session/scope lane. Hosts must use authority methods for memory writes.

Behavior proposals, immutable Skill versions, active registry pointers, and registry-change history share one transaction
and use optimistic revisions across competing authority connections. Persisted JSON, fingerprints, activation provenance,
scope precedence, and chronology are revalidated on read. Pending and validated proposals, active versions, and the
proposal behind every retained version are protected from cleanup; inactive versions, detached terminal proposals, and
change history each require an explicit bounded purge.
