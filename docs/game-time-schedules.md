# Durable game-time schedules

`GameAgent.World` provides durable, timeline-scoped scheduling for long-term
intent. A schedule says when an opaque, schema-validated payload becomes due.
It does not interpret that payload, choose a gameplay effect, advance a clock,
or apply business rules. Games remain responsible for those decisions.

The public boundary is `IWorldScheduleStore`. Both
`InMemoryWorldAuthoritativeTransactionStore` and
`FileWorldAuthoritativeTransactionStore` implement it, and
`NativeWorldRuntime` exposes the same operations through its schedule methods.

## Identity and time

A `WorldScheduleIntent` binds:

- a bounded `scheduleId`;
- `worldId`, `timelineId`, and `timelineEpoch`;
- an exact `GameTimePoint` containing `clockId`, timeline, epoch, and due tick;
- the owning entity ID and incarnation;
- a payload schema ID and version; and
- bounded JSON schema and payload values with canonical digests.

The payload must satisfy its declared JSON schema at construction time. The
store treats the admitted payload as data and never derives a game action from
it.

Each record has a monotonic generation. Its `occurrenceId` is a deterministic
identity derived from the schedule scope, schedule ID, and generation. A
reschedule advances the generation and therefore creates a new occurrence. A
claim release, recovery reassignment, or process restart preserves the current
occurrence.

An owner incarnation is checked against the authoritative world snapshot when
a schedule is created and again when it is claimed. A schedule owned by an
obsolete incarnation cannot begin new work.

## Commands, compare-and-set, and replay

All mutations use a `WorldScheduleCommand` with a bounded, scope-local
`operationId`. The command factories cover create, reschedule, cancel, claim,
release, complete, and explicit claim reassignment.

Operations other than create carry the expected generation. This is the
compare-and-set fence for deterministic cancellation and rescheduling. The
store durably records both accepted and rejected operation receipts:

- replaying the same operation ID with the same request fingerprint returns
  the original receipt;
- reusing it for different input returns
  `world_schedule_idempotency_conflict`; and
- capacity exhaustion fails instead of dropping old receipts.

Cancellation is checked before a mutation is published. The file store checks
again immediately before persistence. Once the checksummed replacement is
published, the operation returns its durable result rather than reporting a
late cancellation as though nothing happened.

`QueryDueAsync` returns bounded pages ordered by:

```text
due tick, then schedule ID, then generation
```

The cursor contains that same ordering key. Queries are isolated to one world,
timeline, epoch, and clock. Active records remain visible while claimed so
recovery code can discover in-flight work; cancelled and completed records are
not due.

When using `NativeWorldRuntime`, schedule creation and rescheduling must name a
clock declared by the activated world package. Claim observations and due
queries cannot be ahead of that clock's authoritative package state. A direct
`IWorldScheduleStore` implementation is the lower persistence boundary, so a
host calling it directly is responsible for supplying already-admitted game
time.

## Delivery and crash recovery

Claiming a due record durably assigns one claimant and returns a claim token.
The token is a coordination capability, not an authentication secret. Complete
and release require the exact generation, occurrence ID, claimant, and token.
A competing claimant receives `world_schedule_claimed_by_another`.

Claims deliberately have no wall-clock lease or implicit timeout. After a lost
acknowledgement or process crash, reopening the store returns the same claim,
claim token, operation receipt, and occurrence ID. If downstream completion is
unknown, recovery must reconcile downstream state using that same occurrence
ID. It must not convert a timeout into a new occurrence.

After reconciliation, a host may:

- replay the original claim operation;
- complete or release the existing claim; or
- explicitly reassign the existing occurrence to another claimant.

Reassignment keeps the occurrence ID and issues a new claim token. It is an
explicit recovery decision, not automatic lease expiry, and it repeats the
authoritative owner-incarnation check before transferring delivery. Only an
explicit reschedule creates another generation and occurrence. A completed
record may be rescheduled when the game intentionally models recurrence.

The file-backed store serializes all store instances with an operating-system
lock. It reloads and validates a bounded, checksummed image under that lock,
writes the next image in the same directory, flushes it, and atomically
replaces the authoritative path. Schedules, operation receipts, authoritative
state, transaction receipts, and event history share that image and capture
boundary. The next image has one deterministic sidecar path; opening the store
reclaims an abandoned image left by a process or power failure, so repeated
crashes do not accumulate full-size snapshots.

This is a local-filesystem durability model. It does not provide distributed
consensus, network-filesystem coordination, or exactly-once behavior in an
arbitrary external service. Its recovery contract prevents the scheduler from
inventing a second occurrence; the game-owned downstream handler must also
deduplicate or reconcile by `occurrenceId`.

## Save, restore, and fork

`NativeWorldSaveBridge` includes schedule records and schedule-operation
receipts in its settled capture. Restore retains active claims and receipts, so
the same operation replays with the same occurrence and token after either
in-memory or file-backed restore.

The schedule section is an optional, versioned extension of the portable save
artifact. An older admitted artifact with no schedule section restores an empty
schedule set. New captures bind schedule counts, completeness, and a digest;
unknown fields, inconsistent records, truncation, or limit violations fail
closed.

A fork rehomes every schedule to the new timeline and epoch, clears active
claims, and drops the parent schedule-operation history. The generation and
terminal status are retained, while occurrence IDs are recomputed in the new
scope. Parent claims and operation IDs therefore cannot claim work in the
fork, and the parent artifact remains unchanged.

## Bounds and trust

`WorldScheduleStoreOptions` bounds schedule count, operation-receipt count, and
aggregate schema-plus-payload bytes. Each schema and payload also has
independent depth, node, string, container, and byte limits. Due enumeration,
save capture, restore admission, and on-disk parsing have separate bounded
pages or collection limits. Limits fail closed; no schedule or idempotency
history is silently trimmed.

The baseline does not automatically delete terminal schedules or compact the
operation ledger. Long-running worlds should size these limits deliberately
and use an application-controlled migration before capacity is exhausted.

Canonical digests detect corruption and inconsistent records. They are not
signatures. Hosts accepting saves or store files from an untrusted party need
their own authenticated envelope and filesystem access controls.
