# Native world save bridge

`INativeWorldSaveBridge` converts a live `NativeWorldRuntime` timeline to
and from the portable `WorldSaveDocument` contract. It supports four
operations:

- capture one settled runtime timeline;
- restore to a new in-memory runtime;
- restore to a new file-backed runtime; and
- derive a deterministic fork with a new timeline identity.

This bridge is authoritative-only. It deliberately does not carry local memory
records, group-interaction membership or transcripts, or durable-presentation
audiences and content. When those settled sidecars must travel with the world,
use the complete
[interactive world bundle](interactive-world-bundles.md) carrier.

The first bridge version intentionally supports
`NativeWorldSaveCaptureMode.RequireSettled` only. It never serializes a
pending transaction or describes pending work as resumable.

## Settled capture

`CaptureAsync` asks the authoritative store for one stable fence. The
store holds its transaction boundary while it reads:

- the authoritative state and coordinate;
- the current entity-incarnation map and complete issued-incarnation ledger;
- every terminal transaction receipt in the timeline scope;
- every event-history record in the timeline scope; and
- every schedule and schedule-operation receipt in the timeline scope;
- the absence of pending transaction ownership in that scope.

A pending operation causes
`native_world_save_pending_transactions`. A store that cannot provide
this atomic view causes `native_world_save_unsupported_store`. The
built-in in-memory and file-backed authoritative stores implement the
capture boundary.

The returned document has no `pendingTransaction` and claims complete
transaction and event history. If the bridge cannot prove those
properties, it fails closed.

## Portable bindings and integrity

The bridge uses `WorldSaveDocument` and `WorldSaveCodec`; it does not
introduce a second outer save format. Its closed bridge metadata and
ordered record stream bind:

- package and interaction-catalog digests;
- world, timeline, timeline epoch, save revision, and state version;
- authoritative state, current entity-incarnation, and issued-incarnation
  ledger digests;
- terminal receipt and event-history counts and a record-stream digest;
- schedule and schedule-operation counts, completeness, and digest;
- a timeline digest and a snapshot digest; and
- fork ancestry, when present, including the parent save digest.

Restore recomputes and checks every binding, count, identity, receipt
fingerprint, event-history relationship, state digest, declared clock,
and canonical record ordering before constructing a runtime or creating
a seed file. Unknown bridge fields, noncanonical integers, incomplete
history markers, conflicting identities, and cross-timeline records are
rejected.

These digests detect accidental damage and inconsistent artifacts. They
are not signatures and do not establish author identity or provenance.
Applications that accept saves from an untrusted party should add an
authenticated envelope or verify the document through their own trust
system before restore.

## Restore behavior

`RestoreInMemoryAsync` fully admits the document before creating the
target store. The new runtime contains the captured snapshot, terminal
receipt/idempotency history, event history, schedules, and schedule-operation
history. Its exact issued-incarnation ledger is restored independently of the
current entity map, so removing an entity does not make an old incarnation
available for reuse. Subsequent execution continues from the same authoritative
fence.
An active schedule claim retains its occurrence identity and claim token.

`RestoreFileAsync` accepts only a new target path in an existing
directory. It validates the complete document first, writes and
flushes a same-directory seed store, reads the seed back, verifies it
against the admitted capture, and publishes it with a no-overwrite file
move. A failed admission or failed seed leaves the target absent. An
existing target or ownership file is never replaced.

Publication uses one deterministic seed image per target and serializes
concurrent restore attempts with a target-scoped operating-system lock.
Abandoned seed, seed-lock, and next-image artifacts are reclaimed before a new
attempt, bounding crash residue instead of accumulating uniquely named full
store images.

Cancellation is honored through admission and seeding. Immediately
before publication, cancellation is checked one final time. Once the
atomic move succeeds, the method completes opening that published
runtime without observing later cancellation; this avoids reporting a
cancelled result after committing a target.

File publication assumes local-filesystem same-directory rename
semantics. The bridge rejects symbolic-link/reparse-point parents when
they are visible and revalidates the path before publication. Hosts
must still protect the parent directory from concurrent topology
changes and should not treat network filesystems as providing stronger
atomicity or durability than the filesystem documents. File contents
are flushed before publication, but managed file APIs do not provide a
portable directory-entry `fsync` guarantee.

## Fork behavior

`ForkAsync` admits a complete source document and requires a distinct
timeline identifier. The derived artifact:

- increments the source timeline epoch;
- resets save revision and state version to zero;
- copies authoritative state, current entity incarnations, and the complete
  issued-incarnation ledger;
- records the source timeline, revision, and save digest as ancestry;
- discards source terminal receipts so old command and operation
  identities cannot claim the fork; and
- rehomes schedules into the new scope, clears active claims, and discards
  parent schedule-operation receipts; and
- deterministically rehomes existing event history into the new
  timeline and epoch.

The fork is derived from the supplied portable document, not from a
later live state. Mutations made after the source capture therefore
remain on the abandoned future and cannot enter the fork. Repeating a
fork with the same source and timeline identifier produces the same
portable bytes.

## Bounds and operating limits

`NativeWorldSaveBridgeOptions` bounds transaction records, event
history, schedules, schedule-operation receipts, current entity incarnations,
issued entity incarnations, JSON item counts, depth, and bytes. The default and
hard issued-incarnation limit is 65,536 entries. New captures encode that ledger
and the current-incarnation map as one canonical packed record. The record uses
a versioned binary layout, safe-ASCII base85, and bounded string chunks. Entity
IDs are stored as their exact UTF-8 bytes, so Unicode, quotes, backslashes, and
control characters keep their existing meaning instead of acquiring a
serialization-specific identifier policy. Current incarnations reference their
exact latest issued records rather than duplicating IDs. A 65,536-entry ledger
whose IDs all occupy the full 192-byte allowance therefore fits the ordinary
save byte, node, string, and container limits when the other save sections are
small, without relaxing those generic limits. At the absolute ledger bound with
4,096 current entities, the packed payload is 13,180,942 bytes and its base85
text is 16,476,180 characters split across four strings. As with every bounded
aggregate, simultaneously filling unrelated save sections can still exhaust
the shared file-byte budget.

Admission also accepts the earlier parallel-array encoding, the short-lived
object-per-issued-incarnation encoding, and older current-only saves. A
current-only save seeds its issued ledger from the current incarnation map; a
newly captured or forked save always uses the packed canonical encoding. Local
file authoritative stores write the same packed incarnation representation and
continue to read their earlier array representation. Packed admission validates
the alphabet, encoded length, zero padding, UTF-8, ordering, uniqueness,
incarnation range, current-record indexes, and exact latest-lifetime binding
before constructing a snapshot. Limits are checked before or during
enumeration and again during admission. Exceeding a bridge collection limit
causes `native_world_save_capacity_exceeded`.

The schedule section is optional for backward compatibility. An older admitted
artifact without it restores an empty schedule set. New captures always emit
the versioned section and bind its complete record set.

The settled bridge does not provide:

- pending transaction checkpoint or resume;
- partial-history restore;
- in-place replacement of an existing file store;
- distributed locking or network-filesystem coordination; or
- artifact authentication.

Callers should keep the portable document and the exact compiled
package available together. A package or catalog mismatch is rejected
instead of being migrated implicitly.

## Minimal usage

```csharp
INativeWorldSaveBridge bridge = new NativeWorldSaveBridge();

WorldSaveDocument save = await bridge.CaptureAsync(runtime);
byte[] bytes = WorldSaveCodec.Write(save);

WorldSaveDocument admitted = WorldSaveCodec.Read(bytes);
NativeWorldRuntime restored = await bridge.RestoreInMemoryAsync(
    activatedPackage,
    admitted);

WorldSaveDocument fork = await bridge.ForkAsync(
    activatedPackage,
    admitted,
    "alternate-timeline");
```
