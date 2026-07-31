# Durable world presentations

Durable presentations are the non-authoritative output side of an interactive
world. They let a game persist dialogue lines, notifications, animation or
audio cues, map annotations, choices, and other typed UI material without
turning those records into game-state mutations.

## Authority boundary

`DurableWorldPresentationPublisher` reads the referenced world receipt through
`ICommittedWorldPresentationEvidenceSource` before it writes anything. The
evidence source is a trust boundary and should query the same authoritative
transaction ledger used by the game.

Publishing fails closed when the receipt is missing or when any of these fields
do not match exactly:

- world receipt ID and digest;
- event occurrence, action, and operation references;
- world and timeline IDs, timeline epoch, save revision, and state version;
- catalog digest, optional committed-state digest, and optional game-time
  point.

Evidence has a typed `WorldPresentationCommitStatus`; only `Applied` is
constructible and accepted by the publisher. Rejected, cancelled, busy, or
unresolved work must return `null`.
A presentation cannot be used as proof that its source world action committed.

## Record model

Each `VerifiedWorldPresentation` contains:

- a stable presentation ID and monotonic content revision;
- the committed source and exact world binding;
- a frozen audience membership scope and revision, with exact entity
  incarnations;
- game-defined privacy and redaction classifications;
- typed JSON content, optional localization data, and bounded media cues;
- producer and derivation provenance;
- a semantic SHA-256 digest covering every semantic field and the committed
  evidence digest.

The model is engine-neutral. A Godot or Unity host maps content kinds and media
cues onto its own scenes, assets, and UI. The framework does not choose widgets,
play animations, or interpret gameplay values.

## Durable file store

`FileWorldPresentationStore` is an append-only, compare-and-swap store.
A new presentation starts at content revision `0` with expected previous
revision `-1`. Later revisions must be contiguous and preserve the source,
world binding, audience, and evidence identity. Exact retries are idempotent;
reuse of the same ID/revision for different content is rejected.
Presentation histories are scoped by the complete world binding, so the same
presentation ID can be reused independently in a save, timeline, epoch, state,
or catalog fork.

Each append is a length-delimited, checksummed frame with a commit marker and a
digest-chain link. Startup truncates an incomplete final frame and rejects
committed corruption, sequence gaps, invalid semantic digests, or broken
revision histories. Once a write may have started, cancellation is deferred
until the bounded frame commit finishes.

The store is intentionally append-only. Use a separate file for each retention
domain and rotate it only at a host-defined boundary where old presentation
history is no longer needed.

## Audience-safe reads and exports

All application reads go through `DurableWorldPresentationReader` and an
injected `IWorldPresentationReadAuthorizer`. The access request contains caller
claims; it is not a grant. The host authorizer must verify the current session,
viewer lifetime, membership, and disclosure policy against authoritative host
state. Only then does the reader create the opaque grant accepted by the store.

A record is returned only when:

- the binding matches the same timeline, epoch, save, state, and catalog;
- the grant names the same membership scope and revision;
- the viewer ID and incarnation are in the frozen audience;
- both privacy and redaction classifications are authorized.

Reads return `WorldPresentationProjection`, not the internal record. The
projection contains only the authorized content and fields already disclosed
by the access request. Its `ProjectionDigest` covers those disclosed fields
only. It never exposes other audience members, raw source identifiers,
provenance metadata, evidence commitments, internal record digests, or the
physical append sequence.

`QueryAsync` and `ExportAsync` are paged and bounded. Their continuation is a
deterministic opaque cursor bound to the exact binding, viewer incarnation,
membership revision, and disclosure classes. It can be rebuilt after store
reload, cannot be replayed by another viewer or query, and does not reveal
hidden physical sequence gaps. Exports include the authorization and opaque
continuation state in their semantic digest. A new
incarnation, membership revision, save fork, timeline fork, or catalog revision
therefore receives no historical content unless the game deliberately supplies
and authorizes the corresponding exact request.

Recovery performs an allocation-free token and collection-count pass before
deserializing a frame. Per-value and aggregate JSON limits, frame-token limits,
record count, physical log size, and a conservative resident-memory estimate
are independent fail-closed capacities. Page item count and aggregate projected
UTF-8 bytes are also bounded. Queries use exact
binding/membership/viewer-incarnation/class postings and a bounded k-way merge;
they project immutable record snapshots after releasing the writer lock.

```csharp
await using var store = new FileWorldPresentationStore(path);
var publisher = new DurableWorldPresentationPublisher(
    authoritativeEvidenceSource,
    store);

WorldPresentationPublishResult result = await publisher.PublishAsync(
    draft,
    expectedPreviousContentRevision: -1,
    cancellationToken);

var reader = new DurableWorldPresentationReader(
    hostReadAuthorizer,
    store);
WorldPresentationPage page = await reader.QueryAsync(
    accessRequest,
    afterCursor: null,
    maxItems: 100,
    cancellationToken);
```
