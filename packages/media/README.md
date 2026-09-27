# @opengameagent/media

Optional provider-neutral output-generation jobs for OpenGameAgent.

The package persists a stable attempt before provider submission and exposes explicit local read, one-shot reconcile,
one-shot cancel, and restart operations. It never polls, retries, resumes, dynamically chooses a provider, imports a
result into a game, or starts another Agent loop. Hosts inject a bounded non-empty registry of uniquely identified
provider Adapters, one job Store, and one resource publisher. Every submission requires the trusted host to select an
adapter with `submit(adapterId, draft)`; there is no implicit single-entry selection. The selected `adapterId` is
persisted in the original job before any provider call. The injected ID generator must return a fresh identity for
every attempt, submission, and bundle request.

Every read, bundle selection, reconcile, cancel, and restart resolves only the adapter identity persisted in that job.
Duplicate job identities conflict across the entire Store and cannot switch adapters. An unknown selection, a removed
adapter after restart, a duplicate registration, or an over-capacity registry fails with a bounded framework error
before provider execution. Provider objects and their endpoint, credential, model, dimensions, format, and watermark
configuration are never serialized into a job. Model-visible Tool arguments must not select an adapter or any of that
provider configuration; a game-owned host wrapper performs the semantic Tool-to-adapter choice.

The optional `@opengameagent/integrations` package supplies a one-shot Pi/OpenRouter image Adapter. The media package
itself remains provider-neutral and has no Pi, OpenRouter, model-catalog, or credential dependency.

Every attempt keeps its own submission identity and terminal evidence. An ambiguous submission remains
`outcome_unknown`; `restart` creates a new attempt only after the previous attempt is terminal or explicitly abandoned.
Cancellation intent is recorded before a provider cancel call. A provider completion racing cancellation is settled by
the first terminal fact admitted by the Store.
After process recovery, explicitly reconciling a still-`submitting` attempt with no durable provider handle marks it
`outcome_unknown` without contacting or resubmitting to the provider. The host must then abandon it before restart.
If caller cancellation lands after the attempt is persisted but before provider submission begins, the attempt ends as
locally `cancelled`, `abandoned`, and cancellation `requested`; this makes no provider-cancellation claim.

If caller cancellation interrupts local resource admission after a provider has already completed, the attempt remains
`settling` with only fully admitted output slots. No partial bundle is visible; a host may explicitly reconcile again
when a durable provider handle exists, or explicitly abandon and restart the attempt.

Successful and partial results publish an immutable bundle only after all referenced resources have been admitted.
Provider handles, requests, credentials, source URLs, and raw errors are absent from job views and result bundles. A
late result may be retained as evidence, but it cannot be selected or imported.

`selectResource` accepts the exact `jobId`, `attemptId`, `bundleId`, output `ordinal`, and `resourceId`. It returns a
frozen resource lineage only when every field identifies one published output in a `complete` or `partial` bundle. A
digest alone does not identify an output slot; a missing, mismatched, failed, or late output has no selection. Selection
reads one job snapshot and does not read resource bytes, call the provider, authorize access, or write game state. Games
place the stable selection and a game-owned destination in an ordinary reliable Action; the Action and game retain
authority, conflict, receipt, and reconcile semantics.

`LocalGeneratedResourceStore` is a media-metadata Adapter over the single neutral
`@opengameagent/resources` original-byte Store. It admits provider `kind`, `mediaType`, and output `ordinal`, delegates
the exact eager/stream/lazy-reader bytes, then decorates the returned digest and length as a `GeneratedResourceRef`.
The underlying content identity contains no media or job semantics, so identical bytes at different output slots share
one object while exact selection lineage remains in this package's durable bundle.

Every publish and read requires explicit byte and media-type budgets plus an `AbortSignal`. The Adapter does not decode,
transcode, inspect MIME content, authorize access, or select which result a game should import. Atomic visibility,
durability, integrity, trusted-root, retention, and repair semantics are owned and documented by
`@opengameagent/resources`.

`MediaJobStore.create` must atomically reject an existing job. Each `modify` callback receives one job snapshot, runs
once and synchronously inside that job's critical section, and either commits its complete return value atomically or
commits nothing. The Store owns a monotonically increasing revision and must not lose competing writes. Fully admitted
output slots and their manifest are committed together; a process failure before that transaction may leave only inert
resource-store objects, never a visible partial bundle.

Both the process-local conformance Store and durable Stores receive the same complete `MediaJobLimits`. This lets a
cold read revalidate the private request, provider handle, attempts, settlements, and bundle links before any provider
operation. The SQLite Adapter additionally requires a host-selected maximum for the complete canonical job snapshot.

All adapter-count, byte, depth, attempt, output, media-type, resource, bundle, and manifest limits are mandatory host
configuration.
The package defines no polling interval, timeout, retry count, retention period, queue capacity, or concurrency default.

Every `MediaGenerationAdapter` registration requires a `MediaCallAdmissionGroup`. Its two limits are explicit host
inputs; sharing one group instance makes multiple adapters consume the same budget:

```ts
const providerAdmission = new MediaCallAdmissionGroup({
  maxConcurrentCalls: hostConcurrentCalls,
  maxQueuedCalls: hostQueuedCalls,
});
const adapters = [
  { adapterId: "image-wide", provider: wideProvider, admission: providerAdmission },
  { adapterId: "image-tall", provider: tallProvider, admission: providerAdmission },
];
```

This limits local provider `start`, `read`, and `cancel` calls, including their admission/persistence handoff. It does
not limit the number of asynchronous jobs already running at a remote provider. A returned `pending` handle releases
call capacity; subsequent reconcile/cancel operations acquire it again. Capacity is released before downstream result
publication. Separate group instances have independent capacity; the group is process-local, so distributed provider
quotas require a host-owned coordinator. There are no automatic retries, timeouts, polling, or background recovery.

`submit` and `restart` reserve capacity before adding a durable job/attempt. When the active and waiting budgets are
full they throw `MediaJobAdmissionFullError` without adding one. Accepted attempts are persisted as `queued`; only an
atomic Store modification may claim `queued` as `submitting` immediately before `provider.start`. This adds a durable
revision transition. A cancellation of a queued attempt commits `cancelled` and removes its live wait slot without
calling `provider.start`. Waiting caller aborts do the same; cancellation after dispatch still uses the existing
abandonment/uncertain-outcome protocol. An active provider that ignores abort holds its call capacity until it settles,
so cancellation cannot accidentally raise actual concurrency. Waiting read/cancel failures preserve provider state;
they are not classified as an unknown provider outcome.

After a process restart, `service.listQueued({ adapterId, afterJobId?, limit })` lists an explicit bounded recovery batch
of content-free job views, ordered by UTF-8 job ID. Pass the last returned job ID as `afterJobId` for another batch.
The Store exposes the same query with validated full snapshots. The host calls `reconcile({ jobId, attemptId })` for
each desired queued attempt; it re-enters that adapter's admission group and atomically claims the existing attempt,
preserving its submission key. Competing resumptions cannot issue the same start twice. Already-`submitting` work
continues to recover as `outcome_unknown`, and existing `pending` handles use provider `read`, never an automatic start.
Use the live service that owns a waiting call for immediate cancellation wakeup; independently opened services share
durable state but do not share local waiter notifications. A waiter still rechecks durable state before any provider
start. Hosts own recovery enumeration and admission for their storage/process topology.

`MediaJobStore.purge({ beforeUpdatedAt, limit, retainJobIds, retainBudget })` deletes at most `limit` unretained jobs
strictly older than the canonical UTC cutoff and returns `{ deletedJobs }`. Every attempt must be terminal or abandoned;
restarting an abandoned job into an active attempt protects the entire job. Both in-memory and SQLite implementations
serialize cleanup with mutations. SQLite validates each candidate's complete persisted snapshot and rolls back the
whole batch on corruption. `retainBudget: { maxCount, maxBytes }` is mandatory; the complete `retainJobIds` array is
counted and charged by total UTF-8 identifier bytes before any snapshot, Set, or SQL parameter serialization. All listed
jobs survive, including their bundles and output settlements. The host must include archived/external job references
and drain local lifecycle operations before retiring terminal or abandoned jobs. Purging a job does not cancel a remote
provider operation and does not remove resource bytes.

`LocalGeneratedResourceStore.collectGarbage` delegates to the neutral resource Store with the same cutoff, deletion,
scan, signal, and retain-budget fields; its complete reference field is `retainResourceIds` instead of `retainSha256`.
Every local `resourceId` is the content SHA-256. Build this set from all surviving jobs, archived bundles, and external
references, including resources shared by multiple jobs or slots. Stop and drain every resource/reference writer while
taking the snapshot and collecting; the host supplies cross-instance/process maintenance exclusion. The resource
Store README describes bounded preflight, crash-temporary selection, identity checks, partial-failure retries, and the
content-free deletion result. Job and resource cleanup are explicit separate steps because the media Store cannot
discover a game's durable archives or external references.

The repository-level `benchmark:media` workload streams one explicitly configured 128 MiB resource through
`LocalGeneratedResourceStore`, persists the resulting bundle through `SqliteMediaJobStore`, and verifies exact bytes and
selection lineage after both stores are cold-opened. The provider is deliberately unavailable during restart
verification. The workload size and timeout remain acceptance-profile inputs, not package defaults or SLAs.
