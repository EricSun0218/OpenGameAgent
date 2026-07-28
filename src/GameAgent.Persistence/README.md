# GameAgent.Persistence

`FileSessionStore` is the portable, managed-code durable store used by the
in-process Godot and Unity runtimes. It has no native database dependency.

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

Only one writer can open a journal. Calls on that writer are serialized.
After any error that may have occurred after bytes were written, that instance
enters a faulted state and must be reopened before another append.

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
