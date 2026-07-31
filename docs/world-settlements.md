# Durable world settlements

`WorldSettlementCoordinator` is a game-neutral outbox between one committed
authoritative world receipt and caller-authored non-authoritative side effects.
It can deliver explicit drafts to memory, an existing group interaction, and
durable presentation storage. It does not choose who remembers an event, what
a group should hear, what text should be shown, or whether any delivery should
exist. Those are game rules and remain in game code.

The coordinator never executes an agent, calls a model, or repeats the world
action that produced the receipt.

## Receipt and authority gates

A new `WorldSettlementPlan` carries all of the following exact values:

- settlement ID and stable delivery operation IDs;
- authoritative world receipt ID and receipt digest;
- world, timeline, timeline epoch, save revision, state version, and catalog
  digest;
- optional committed-state digest and exact game-time coordinate;
- an immutable snapshot of the complete committed receipt evidence, including
  its semantic digest;
- exact audience membership scope, membership revision, and entity
  incarnations for every delivery.

`SettleAsync` reads `ICommittedWorldPresentationEvidenceSource` before it
creates an outbox record. The source, binding, applied status, and evidence
digest must all match. Missing, rejected, unresolved, or differently bound
evidence creates no outbox record and reaches no sink.

For the built-in authoritative world runtime,
`WorldCommandPresentationEvidence.CreateApplied(receipt, gameTime)` converts a
terminal applied `WorldCommandReceipt` into the exact source, resulting
coordinate, committed-state digest, and compact receipt evidence expected by
this boundary. The compact evidence stores only identities and digests; it
does not copy a potentially private typed effect result into presentation or
outbox data. `NativeWorldCommittedEvidenceSource` is the shipping adapter for
`NativeWorldEngineSession`. It reads the active generation's authoritative
receipt ledger through a read-only session capture and constructs evidence
with `WorldCommandPresentationEvidence.CreateApplied`. It returns no evidence
for a missing, rejected, cancelled, cross-scope, or replaced-generation
receipt. It never trusts receipt evidence embedded in a caller-authored plan.
The verified evidence snapshot is then part of the plan digest and is persisted
with the outbox. `ResumeAsync` uses that immutable snapshot, so receipt-ledger
compaction after a crash cannot strand an already admitted reconciliation.
The authority lease still has to prove that the snapshot's world coordinate
is current and remains stable while a sink is called.

`IWorldSettlementAuthorityGuard` is the host-owned current-state boundary. Its
`AcquireAsync` implementation must validate the complete
`WorldSettlementAuthorityRequest` against authoritative game state. An
acquired lease that allows a delivery must keep the exact world binding,
membership revisions, and entity incarnations stable until the lease is
disposed. A check that releases its lock before returning is not sufficient;
a multi-process host needs an equivalent distributed lease. Each delivery
validation receives the complete immutable typed delivery, its semantic
digest, and its exact audience claim, so host policy can inspect ownership and
content classification. The framework deliberately does not encode a game's
faction, visibility, consent, ownership, or event-resolution rules in this
guard.

`NativeWorldSettlementAuthorityGuard` supplies the mechanical part of that
boundary for `NativeWorldEngineSession`. Its exclusive lease pauses new native
admission, drains already admitted work, and verifies the exact active session
generation, world, timeline, epoch, save revision, state version, catalog
digest, committed-state digest, receipt, and every claimed entity
incarnation. Package and save replacement and authoritative world operations
cannot pass the lease. The guard re-reads the receipt from the leased
generation, so a generation swap between the coordinator's initial evidence
check and authority acquisition cannot authorize stale evidence.
Each validation also compares the delivery's complete semantic digest, not
only its operation ID and sink kind. The digest is deterministic identity for
exact comparison; it is not a signature or proof of authenticity.
Session load and shutdown APIs fail fast when invoked from that session's
admitted `RunAsync` callback, because a draining transition cannot wait on the
operation that initiated it.

When a binding claims game time, the native guard can mechanically verify it
only against authoritative time carried by the receipt's event occurrence.
A non-event receipt that carries no authoritative time cannot accept a
caller-supplied game-time claim and fails closed.
`NativeWorldCommittedEvidenceSource` automatically projects an applied
receipt's occurrence time into its binding; caller code cannot omit or replace
that authoritative time.

Mechanical authority is not game policy. Native state can prove an entity
incarnation, but it cannot infer a group's current membership revision,
visibility, ownership, faction rules, or consent. A single-member `private`
audience may therefore use the mechanical check alone. A multi-member or
non-private audience fails closed unless the host supplies an
`INativeWorldSettlementAudiencePolicy`. Its `AcquireAsync` returns an
`INativeWorldSettlementAudiencePolicyLease`; that lease validates each typed
delivery claim and must keep every membership or game-policy fact used by an
allow decision stable until disposal. This shape lets a host compose a
process-local or distributed group-store lease without coupling the native
world package to a particular group-store implementation.
Policy `AcquireAsync`, lease `ValidateAsync`, and lease `DisposeAsync`
callbacks also cannot acquire a second settlement lease from the same session.
That same-flow reentry fails fast instead of waiting on the policy's own native
fence.

A policy implementation must not hold a non-reentrant mutation gate that
blocks the coordinator's later call to the same memory, group, or presentation
sink. A policy coupled to such a store needs an owner-aware, reentrant, or
handoff-capable lease, or it must rely on the sink's final compare-and-swap
revalidation. The shipping guard never acquires a sink gate. The effective
order is outbox ownership, native generation fence, optional policy lease,
then each sink's short-lived operation gate.

For a group delivery, the coordinator also reads the session immediately
before append and compares its explicit world/timeline/epoch/save binding,
exact group ID, open status, session revision, membership revision, members,
roles, and entity incarnations. An unbound or cross-timeline session is
rejected. The group store's compare-and-swap append closes the remaining race.
A late membership or incarnation change therefore rejects the append without
disclosure.

## Durable lifecycle

Every delivery begins as `Pending`. Before calling a sink, the coordinator
durably changes it to `Reconciliation`. This is the dispatch intent:

1. `Pending` means no sink call was allowed yet.
2. `Reconciliation` means a sink call may not have started, may be in flight,
   or may already have committed.
3. `Applied` means the stable sink operation was confirmed.
4. `Rejected` means a deterministic authority, binding, capacity, revision, or
   payload conflict prevented the delivery.

`ResumeAsync(settlementId)` loads the persisted plan and processes only
deliveries not already `Applied`. It retries a `Reconciliation` delivery only
with its original operation ID and payload. It never redoes an authoritative
world action or model request.

Caller operation IDs are local to one settlement plan. Before dispatch, the
coordinator derives bounded sink identities from the settlement ID, local
operation ID, plan digest, and complete delivery digest. The sink idempotency
contracts are:

- memory requires `IIdempotentAtomicMemoryBatchStore`; the derived identity is
  its durable memory commit ID;
- group append receives a settlement-scoped derived operation ID, preventing
  two plans that reuse the same local operation name from colliding in one
  session;
- presentation uses its stable presentation ID, content revision, and expected
  previous revision through `IWorldPresentationStore`.

If a process stops after a sink commits but before the outbox records
`Applied`, the same delivery is safe to retry. A previously committed group
operation can be confirmed from the group's operation ledger without
re-disclosing it. If current authority can no longer be proven for another
uncertain sink, the record remains `Reconciliation`; it is not falsely marked
rejected or applied.

`Reconciliation` dominates `Rejected` in the aggregate record stage. This
matters if two authorized workers race on different delivery CAS operations:
one rejected delivery cannot hide or orphan another delivery whose dispatch is
still uncertain. Recovery skips the rejected item and continues reconciling
the uncertain one.

There is no cross-store distributed transaction and no automatic rollback.
Earlier deliveries may be `Applied` when a later delivery is `Rejected`.
Games that require all effects in one authoritative transaction should put
those effects in their world store instead of using this outbox.

## Privacy boundary

Memory, group, and presentation payloads are separate caller-owned values.
The coordinator does not derive group or presentation content from memory.
A `WorldSettlementMemoryDelivery` must have a `private` audience containing
exactly one entity incarnation. Every memory upsert must carry committed
provenance for the plan's exact world, timeline, timeline epoch, save revision,
receipt, and private observer incarnation. When the committed binding includes
game time, it must also carry a game-time window on that epoch containing the
receipt time. Timeless worlds may leave the window absent; epoch isolation
remains explicit in `MemoryProvenance` and `MemoryQuery`. A timeline reset that
reuses its ID therefore cannot inherit the prior epoch's settled memory. Group
messages must name that receipt as their causation. Presentation drafts must
use the plan's exact receipt source and world binding.

Before a private upsert reaches a general-purpose memory store, its physical
memory ID is deterministically namespaced by world, session, timeline, epoch,
scope, and exact owner incarnation. Two worlds, branches, actors, or
incarnations may therefore reuse the same caller-local memory ID without
overwriting each other. The caller-local record remains in the immutable plan
and authority claim; search results expose the namespaced durable ID.

Unscoped memory delete-by-ID mutations are not admitted. `IMemoryStore` has no
ownership-aware delete CAS, so accepting such a mutation could let an
otherwise authorized actor delete another actor's private record. Games should
perform forgetting, erasure, or ownership transfer through a host-owned memory
lifecycle that verifies the existing record's scope/provenance and commits it
atomically.

This structural separation prevents the framework from accidentally copying
private memory into a broader sink. The game still owns the content-selection
policy and must not place a private value in a group or presentation draft.

## Store choices and ownership

`InMemoryWorldSettlementStore` is a bounded embedded/test implementation.
`FileWorldSettlementStore` is the durable local-file baseline. It uses
checksummed append-only frames, a commit marker, monotonic revisions, a digest
chain, optimistic compare-and-swap, bounded keyset pagination for unsettled
enumeration, and the same persistent writer-lock sidecar as the other local
stores. Startup
truncates only an incomplete final frame and rejects corruption in the
committed prefix.

Both built-in stores implement `IWorldSettlementQuiescenceSource`. Its
exclusive lease is acquired atomically with proving that the unsettled index
is empty and blocks new begin/transition operations until disposal. Complete
interactive-world bundle capture uses that lease as the outermost fence,
followed by memory, group, and presentation store gates in fixed order. A
`Reconciliation` sink call therefore cannot be mistaken for a settled
cross-store snapshot, and new dispatch cannot start halfway through capture.
Each coordinator exposes an opaque `WorldSettlementTopology` for that exact
outbox and sink set. It has no public constructor. Coordinators with the same
complete store set share the topology; partially overlapping sets are
rejected. Bundle capture consumes this topology instead of accepting raw
stores, preventing an empty substitute outbox from fencing another
coordinator's sidecars.

`ListUnsettledAsync` returns a `WorldSettlementPage` of payload-free
`WorldSettlementSummary` entries. A recovery worker explicitly calls
`ReadAsync(settlementId)` (or `ResumeAsync`) only for work it owns, avoiding
enumeration-time cloning or disclosure of private payloads. Workers must pass
each opaque `ContinuationCursor` into the next
`WorldSettlementListRequest` until `HasMore` is false. This lets a worker move
past a blocked prefix through an ordered unsettled index in
`O(log N + page size)`; starting a later sweep with no cursor also discovers
records inserted before an earlier cursor.

Only one process may own a file-store writer. One application-defined
settlement lifecycle owner should call `SettleAsync`/`ResumeAsync` for a given
settlement ID. Concurrent callers remain safe through store CAS and sink
idempotency, but duplicate dispatch work is possible. A host using multiple
processes should route one settlement ID to one owner or implement
`IWorldSettlementStore` with its own distributed ownership lease.

The file is not encrypted. Choose save/profile-specific paths according to the
game's privacy, export, fork, backup, and deletion policy. Back up the data file
only while its writer is stopped; do not delete or replace an active
`.writer.lock` sidecar.

The append-only baseline retains terminal plans, including private payloads,
and currently has no online prune or compaction API. A production host must
rotate whole stopped save/profile files before record, frame, or log limits are
reached, and delete retired files according to its privacy policy. Before
persisting a dispatch intent, the file store reserves one worst-case terminal
frame; if that reservation does not fit, it leaves the delivery `Pending` and
never calls the sink.

## Minimal composition

```csharp
await using var outbox = new FileWorldSettlementStore(path);
var nativeSettlements = new NativeWorldSettlementComposition(
    nativeSession,
    policy: gameAudiencePolicy); // Optional for private single-member claims.

var coordinator = nativeSettlements.CreateCoordinator(
    outbox,
    memory: memoryStore,
    groups: groupStore,
    presentations: presentationStore);

CommittedWorldPresentationEvidence evidence =
    await nativeSettlements.EvidenceSource.ReadCommittedAsync(
        receipt.ReceiptId,
        cancellationToken)
    ?? throw new InvalidOperationException(
        "The native receipt is not active and applied.");

// Build the explicit plan from this evidence and the game's separately
// selected payloads and audience claims.
WorldSettlementRecord result =
    await coordinator.SettleAsync(plan, cancellationToken);

if (result.Stage == WorldSettlementStage.Reconciliation)
{
    result = await coordinator.ResumeAsync(
        plan.SettlementId,
        cancellationToken);
}
```

Hosts using `NativeWorldEngineSession` do not need to maintain a second receipt
map or write an always-allow authority adapter. Supplying no game audience
policy is deliberately useful only for exact single-entity private delivery.

Plans and file frames have hard delivery, audience, JSON-node, UTF-8 byte,
record, mutation-frame, resident-memory, and log-size limits. Unsettled listing
also requires an explicit result bound, and the file store applies its JSON
token bound before writing as well as during recovery. Raising a bound changes
admission only; it does not change game semantics.
