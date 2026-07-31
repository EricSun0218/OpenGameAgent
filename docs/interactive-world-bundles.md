# Interactive world bundles

`InteractiveWorldBundle` is the complete version-one carrier for a settled
interactive world. It combines the native authoritative `WorldSaveDocument`
with bounded snapshots of:

- committed local memory;
- stable open or closed group interactions; and
- verified durable presentations.

`NativeWorldSaveBridge` remains the authoritative-only boundary. It does not
capture memory contents, group membership or transcripts, or presentation
audiences. Use that bridge when only authoritative simulation state is
required. Use a bundle when a save, copy, transfer, or fork must retain the
complete settled interactive-world slice.

## Capture fence

Capture accepts a `NativeWorldRuntime` and the opaque
`WorldSettlementTopology` exposed by the coordinator that owns the sidecars.
The topology has no public constructor or raw-store factory. It binds the
coordinator's exact outbox, memory, group, and presentation stores, and
partially overlapping coordinator store sets are rejected. A caller therefore
cannot substitute an empty outbox while retaining another coordinator's
sidecars.

The outbox must implement `IWorldSettlementQuiescenceSource`, and every
configured sidecar must be the corresponding built-in local file store.
Unsupported composition fails with
`interactive_world_bundle_quiescence_required` or
`interactive_world_bundle_topology_unsupported`.

The bundle first acquires an exclusive outbox lease atomically with proving
that no `Pending` or `Reconciliation` settlement exists. It then acquires the
memory, group, and presentation mutation gates in that fixed order and keeps
all four leases until assembly finishes. New settlement dispatch and direct
file-sidecar writes therefore cannot cross the snapshot. It also captures the
authoritative save before and after the sidecars and requires the two
canonical saves to be identical. Capture fails when:

- the authoritative store has pending ownership or changes across the fence;
- a memory is uncommitted, lacks an exact timeline epoch, is from another
  world/timeline/epoch, is from a later save, or has game time from another
  timeline epoch;
- a group interaction lacks an explicit `GroupInteractionWorldBinding`, is
  from another world/timeline/epoch, or was created after the captured save;
- a presentation belongs to another world, timeline, epoch, catalog, or a
  later authoritative coordinate;
- a presentation does not reproduce exactly from a terminal applied receipt
  in the native authoritative ledger;
- an entity reference names an entity lifetime the authoritative timeline
  never issued;
- a presentation that names a bundled group session does not match that
  session's exact membership revision; or
- any configured count, byte, JSON-depth, JSON-token, group-history, or entity
  reference limit is exceeded.

An open group is stable state, not pending work. Its exact session revision,
membership revision, audience history, and operation ledger are captured and
can continue after import. Capture is intentionally settled-only at mutation
boundaries: it does not checkpoint a model request, an agent turn, an unknown
tool outcome, a pending world transaction, or an unsettled sidecar delivery.
A host should finish or reconcile those operations before requesting a bundle.
The topology guarantees that all captured sidecars come from the coordinator
whose outbox supplied the quiescence lease.

## Entity lifetimes and current authority

The authoritative snapshot keeps two separate incarnation views. The current
entity-incarnation map is the action and observation authority fence: an
identity may act only while its exact incarnation is current. A bounded,
immutable issued-incarnation ledger records every exact lifetime ever issued
on the timeline. Removing an entity clears only the current map, and advancing
from incarnation 1 to 3 does not imply that incarnation 2 ever existed.
New issuance must be greater than that entity's prior maximum, so the skipped
incarnation 2 can never be issued later or retroactively legitimize a sidecar.

Private memory perspectives, complete group membership and message history,
and presentation audiences are validated against that exact issued ledger.
Historical sidecars can therefore survive an entity upgrade or removal
without granting the replacement incarnation access to the predecessor's
private data. An unknown entity or an unissued future or skipped incarnation
is rejected. The ledger is included in native save counts and digests,
persists through file-store restart, and is copied into forks. Legacy native
saves and local authoritative-store images that predate the ledger seed it
from their current map; they cannot manufacture any additional history.

## Deterministic archive

The archive has a fixed header, a canonical JSON manifest, and four
length-delimited entries in a fixed order. The manifest binds:

- package ID, content version, and package digest;
- world and timeline IDs, timeline epoch, save revision, and state version;
- catalog, authoritative-state, and native-save digests;
- export mode and sidecar item counts; and
- the exact length and SHA-256 digest of every entry.

Each sidecar also carries a digest of the complete authoritative binding.
Every admitted group session additionally carries its intrinsic world,
timeline, epoch, and creation/rebind save revision. Legacy group files without
that binding can still be opened for explicit migration, but complete bundle
capture rejects them rather than guessing a scope. The header binds the
manifest digest. Admission rejects truncation, trailing
bytes, reordered or missing entries, noncanonical sidecar JSON, duplicate JSON
properties, digest mismatches, scope mismatches, and resource-limit failures
before a target directory can be published. These hashes detect corruption and
inconsistent assembly; they are not signatures. Authenticate archives at the
application boundary when author identity matters.

## Privacy modes

`PrivateLocal` carries every admitted sidecar record. The files are not
encrypted and should remain inside the game's protected save boundary.

`PublicExport` uses a fixed, fail-closed policy. It carries the authoritative
native save but emits empty memory, group, and presentation sidecars. The
capture API accepts no viewer, audience, membership grant, privacy-class list,
or redaction-class list, so a caller cannot widen disclosure by supplying a
more permissive audience. This is deliberately conservative: arbitrary memory
scopes and group payloads have no game-neutral proof of public visibility, and
a verified presentation's internal record contains audience, receipt, and
provenance identities that its public projection intentionally hides.

The authoritative state itself may contain game-private data. A host offering
public save sharing must design that authoritative schema for sharing or add a
separate game-owned export/migration format. The framework does not silently
rewrite authoritative state and then claim the original save digest.

## Import and atomic publication

Import requires the exact activated package. It fully parses and validates the
archive, restores the native save to an in-memory runtime, compares every
manifest binding, reconstructs every sidecar object, validates privacy and
capacity, rechecks every presentation against the receipt ledger embedded in
that native save, and preflights target store counts before touching the
target path.

It then creates one fixed-name sibling seed directory while holding a
target-scoped operating-system lease. The native store and all three sidecar
stores are written, closed, reopened, and checked for semantic parity. A final
same-parent `Directory.Move` publishes the directory without replacement.
Cancellation is checked immediately before that move and not after it. A
failed admission, failed write, failed reopen, cancellation, corrupt entry, or
existing target leaves the target absent and never replaces existing data.

The operation assumes local-filesystem same-directory rename semantics. It
rejects visible symbolic-link/reparse-point parents. It does not claim
distributed locking, network-filesystem atomicity, directory-entry `fsync`, or
protection from an untrusted process that can concurrently rewrite the parent
directory.

The imported directory contains:

```text
world.store
memory.store
groups.store
presentations.store
```

Every store is closed before publication and can be opened by a later process.
Persistent `.writer.lock` sidecars may also exist; they contain no game data
and preserve each store's single-writer contract.

## Forks

`ForkAsync` derives the native fork from the supplied archive, never from a
later live runtime. Work committed after the source archive was captured is
therefore an abandoned future and cannot enter the fork.

The native bridge assigns the new timeline, increments its epoch, resets save
and state revisions, rehomes event history and schedules, and records ancestry.
Bundle memory provenance and game-time windows are rebound to that new
timeline/epoch and revision zero. Open and closed group sessions retain their
exact entity incarnations, frozen membership histories, audiences, revisions,
and lifecycle status; their intrinsic world binding and create-operation
evidence are explicitly rebound to the fork timeline/epoch and revision zero.
Opaque game payloads are never rewritten heuristically.

Verified presentations are intentionally not copied to a fork. Their evidence
digest proves a parent receipt at the parent coordinate, and relabeling it as
verified on a new timeline would fabricate evidence. The fork starts with an
empty presentation store and the host may regenerate presentations from valid
fork receipts.

## Minimal use

```csharp
var coordinator = new WorldSettlementCoordinator(
    committedReceiptEvidence,
    hostAuthorityGuard,
    settlementOutbox,
    memoryStore,
    groupStore,
    presentationStore);

var source = new InteractiveWorldBundleCaptureSource(
    runtime,
    coordinator.Topology);

InteractiveWorldBundleArtifact artifact =
    await InteractiveWorldBundle.CaptureAsync(source);

InteractiveWorldBundleImportResult imported =
    await InteractiveWorldBundle.ImportAsync(
        activatedPackage,
        artifact.GetBytes(),
        newSaveDirectory);
```
