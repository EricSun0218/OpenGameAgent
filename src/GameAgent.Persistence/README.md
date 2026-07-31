# GameAgent.Persistence

`FileSessionStore` is the portable, managed-code durable store used by the
in-process Godot and Unity runtimes. It has no native database dependency.

`FileMemoryStore` provides the same portable durability boundary for
`IMemoryStore`. It keeps the deterministic, embedding-free search behavior of
`DeterministicMemoryStore` while recovering committed memories after a process
restart.

`FileWorldPresentationStore` provides an append-only presentation ledger for
typed, non-authoritative world output. It retains exact world/save/catalog and
audience-incarnation bindings, supports idempotent content-revision CAS and
audience-safe paged export with query-bound opaque cursors, truncates torn tail
frames, and rejects committed corruption. Exact viewer/class postings keep
sparse reads bounded without exposing physical record gaps. Recovery bounds raw
JSON before DTO allocation and enforces a separate resident-memory estimate. See
[durable world presentations](../../docs/durable-presentations.md).

`InteractiveWorldBundle` is the settled copy/import/fork boundary that carries
the native authoritative save together with bounded memory, closed-group, and
verified-presentation snapshots. It restores all four stores into a new
directory and publishes that directory only after complete admission, seed
write, reopen, and parity verification. Public export uses a fixed fail-closed
sidecar redaction policy and accepts no caller audience grant. See
[interactive world bundles](../../docs/interactive-world-bundles.md).

## Commit boundary

Each append is one length-delimited frame:

```text
magic | payload length | CRC-32 | UTF-8 JSON payload | commit marker
```

The payload contains the canonical per-run sequence and revision plus either
one `RuntimeEvent` or an ordered event batch. Every member of a batch receives
its own contiguous sequence and revision, while the commit marker covers the
whole batch. This is the write-ahead boundary used before a multi-action
dispatch and for terminal event pairs. Existing single-event frames remain
readable.

An append is acknowledged only after the complete frame has been written and
flushed. `FlushToDiskOnAppend` defaults to `true` and invokes
`FileStream.Flush(true)`.

At startup, an incomplete final frame is truncated. A committed frame with a
bad checksum, invalid JSON, a sequence gap or a revision gap fails closed with
`JournalCorruptionException`; it is never silently discarded.

New journal frames use format version 3. Versions 1 and 2 remain readable.
Startup has one narrow compatibility rule for the previous writer's
`reconciling` duration-deadline checkpoint; that rule is enabled only for an
older frame version. Version 3 and later checkpoints use the strict lifecycle
validator, so newly written logs cannot invoke the legacy transition.

Format 3 also binds newly computed semantic identities to the typed,
length-delimited digest scheme. The scheme separates strings, integers,
lists, and canonical JSON and frames every variable-length value. Completed
older history remains readable, but a nonterminal version 1 or 2 run whose
resume contract contains an older digest must be restarted rather than
silently migrated.

Only one writer can open a journal. Calls on that writer are serialized.
After any error that may have occurred after bytes were written, that instance
enters a faulted state and must be reopened before another append.
Writer ownership uses a persistent `<data-path>.writer.lock` sidecar so shared
readers never contend with locks on journal bytes. The sidecar contains no game
or agent data and may remain after a clean shutdown or process crash; the
operating system releases ownership with the handle. Do not delete or replace
the sidecar while a writer may be active. Backups and imports need only the
data file. Use one stable, host-owned local path; opening the same data inode
through symlink or hardlink aliases is outside the single-writer contract, as
is coordination through an arbitrary network filesystem.

## Capacity and rotation

The append-only files have hard admission limits so a long simulation or a
large NPC population cannot make startup replay and recovery grow without a
bound:

| Limit | Default | Meaning |
|---|---:|---|
| `FileJournalOptions.MaxJournalBytes` | 256 MiB | Physical journal length |
| `FileJournalOptions.MaxTotalCommittedEvents` | 100,000 | Events across all run streams |
| `FileJournalOptions.MaxEventsPerRun` | 25,000 | Events in one run stream |
| `RunRecoveryOptions.MaxEventsPerRun` | 25,000 | Maximum events a recovery call will admit |
| `FileMemoryStoreOptions.Capacity` | 10,000 | Live memory records |
| `FileMemoryStoreOptions.MaxFramePayloadBytes` | 1 MiB | One single or batch mutation payload, including metadata |
| `FileMemoryStoreOptions.MaxLogBytes` | 256 MiB | Physical memory-log length |
| `FileMemoryStoreOptions.MaxMutationFrames` | 100,000 | Single or batch mutation history frames |

Equality is allowed. The first append beyond a limit throws
`JournalCapacityExceededException` or
`MemoryStoreCapacityExceededException` before any bytes are written, so the
writer does not enter its uncertain/faulted state. An existing file above the
configured startup limit fails with the same capacity exception, not a
corruption exception. Atomic journal and memory batches are admitted as a
whole.

Live-record admission retains the core store contract: exceeding `Capacity`
during an upsert throws `RuntimeContentLimitException` with
`memory_capacity_exceeded`. Reopening a committed file with a lower `Capacity`
throws `MemoryStoreCapacityExceededException` and leaves the file unchanged.

Keep `FileJournalOptions.MaxEventsPerRun` and
`RunRecoveryOptions.MaxEventsPerRun` aligned. Standard builder users can set
the latter with `GameAgentRuntimeBuilder.WithRecoveryOptions(...)`. Raising a
limit changes resource admission only; it does not rewrite the file.

Neither store automatically deletes, compacts, or rotates data. For a journal,
stop the writer and begin a new file only at an application-defined epoch where
no run in the old file must be resumed; archive the old file according to the
game's save/retention policy. If an active run must remain recoverable, retain
the journal and raise the explicit limits instead of dropping history.

For memory, stop the writer, rebuild the live records from
application-authoritative data into a new `FileMemoryStore`, validate the new
record set, and let application code switch files and archive the old log. This
intentionally resets the mutation history while preserving the rebuilt
records. `FileMemoryStore` does not expose an unbounded "export everything"
operation. Do not replace a live file in place.

## Idempotency and CAS

`AppendAtomicAsync` assigns sequence and revision. Its
`expectedRunRevision` argument provides optimistic compare-and-swap:

- a new run starts at revision `0`;
- the first event receives sequence `0`, revision `1`;
- exact event-id retries return the original location with
  `WasDuplicate = true`;
- exact ActionRequest or ActionReceipt retries are also idempotent even when
  the caller's expected revision is stale after an uncertain commit;
- reuse of an id or operation revision for different content fails closed.

Receipt equivalence ignores `receivedAt`, which is local transport metadata and
can legitimately change when the host is queried again. Authoritative fields,
including status, result, state diff and commit time, must remain identical for
the same Receipt revision.

The returned `JournalAppendResult`, not the sequence supplied on the input
DTO, is authoritative.

`AppendAtomicBatchAsync` applies the same compare-and-swap and idempotency
rules to an ordered list. A torn tail exposes none of a new batch. Retrying a
fully committed batch returns each member's original location.

## Operation recovery

Durable `action.requested` and `action.received` events are projected into an
operation ledger during both append and startup replay.

`ReadPendingOperationsAsync` returns operations without a Receipt or whose
latest Receipt is `unknown`. `ReconcileReceiptAsync` atomically validates and
appends a Receipt:

- a request must already exist;
- operation and run ids must match;
- Receipt revisions cannot move backwards or conflict;
- a terminal operation cannot regress to `unknown`;
- `succeeded`, `rejected`, and `failed` remove the operation from the pending
  query.

The game still owns authoritative mutation and operation-id deduplication in
its save data. This journal provides the runtime side of recovery; it does not
make two independent files into a distributed transaction.

## Persistent memory

`FileMemoryStore` writes every single mutation or atomic mixed mutation batch
as a length-delimited, checksummed frame with a final commit marker. A frame is
acknowledged only after it has been flushed; durable disk flushing is enabled
by default. Startup replays contiguous revisions, truncates an incomplete
final frame, and rejects committed corruption with
`MemoryStoreCorruptionException`. A batch has one revision and one commit
marker, so recovery exposes every batch member or none.

Calls on one instance are serialized and only one writer can open a file.
`UpsertAtomicAsync` and `DeleteAtomicAsync` accept an optional
`expectedRevision` for compare-and-swap. A mismatch throws
`MemoryStoreRevisionConflictException` without writing. `Revision` and
`GetRevisionAsync` expose the last committed mutation revision.
`ApplyAtomicBatchAsync` implements `IAtomicMemoryBatchStore`;
`ApplyAtomicBatchWithRevisionAsync` adds compare-and-swap and returns the
committed revision plus ordered per-mutation results. Batch validation rejects
duplicate IDs and invalid or oversized collections before writing. A batch of
only missing-record deletes is a no-op and does not advance the revision.
`ApplyIdempotentAtomicBatchAsync` implements
`IIdempotentAtomicMemoryBatchStore` for runtime outboxes. The store computes the
batch digest itself and persists the commit identity in the same frame. Retrying
the same identity and payload, including after restart, writes no frame and does
not advance the revision; reusing an identity for another payload throws
`MemoryBatchIdempotencyConflictException`. An idempotent all-no-op batch still
writes one settlement frame so its deduplication evidence survives restart.
If an I/O error occurs after writing begins, the instance fails closed because
the caller cannot know whether the commit marker reached disk. Dispose and
reopen it; recovery exposes either the complete mutation or the prior revision,
never a partially applied record.

`FileWorldSettlementStore` is the local durable outbox for
`WorldSettlementCoordinator`. It persists the complete caller-authored
delivery plan and every per-sink transition, uses exact plan-digest CAS, and
recovers only a committed frame prefix. See
[durable world settlements](../../docs/world-settlements.md) for receipt,
authority, privacy, lifecycle-owner, and multi-process responsibilities.

Use a separate memory file for each save/profile boundary whose memories must
be deleted, copied, or rolled back together. The file is not encrypted and
must not contain credentials. `Capacity` and `MaxFramePayloadBytes` are hard
local resource limits; an existing file must be reopened with limits large
enough to admit its committed contents. `Capacity` limits live records, while
`MaxMutationFrames` limits append history, including deletes and overwrites.
