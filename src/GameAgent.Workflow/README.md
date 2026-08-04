# GameAgent.Workflow

`GameAgent.Workflow` is an optional composition plane for bounded OpenGameAgent
and background orchestration. Core agent execution and authoritative game
correctness do not depend on it. Workflow steps call registered adapters
around those existing boundaries; they do not replace runtime journals,
transactions, receipts, or reconciliation rules.

The compiler accepts a static graph of `Step`, `Foreach`, `Reduce`, and `Loop`
stages. Sequence and parallel execution are expressed with `dependsOn` edges.
Compilation rejects duplicate identifiers, missing dependencies, cycles,
multiple terminal sinks, open schemas, and unbounded composite stages.

Runs use canonical definition and payload digests, deterministic instance
identifiers, revision compare-and-swap, persisted cancellation intent, and
owner leases with fencing epochs. A call is committed as `Started` before
dispatch. After interruption, that generation is passed to `RecoverAsync`;
it is not dispatched again through `ExecuteAsync`, and stale generations
cannot publish late results.

Every graph is bounded by stage, dependency, parallelism, foreach-item,
loop-iteration, external-call, attempt, JSON-byte, retained-output, schema,
and wall-clock limits. Cancellation is cooperative at an executor boundary;
persisted cancellation and fencing still prevent a late result from becoming
authoritative.

## Run stores and their evidence boundaries

`InMemoryWorkflowRunStore` is thread-safe but process-local. It is suitable
for tests and single-process operation where loss on process termination is
acceptable. It provides no cross-process exclusion or restart evidence.

`FileWorkflowRunStore` preserves the same revision, lease-owner, fencing,
expiry, renewal, and cancellation contract on a local filesystem:

- Each run is a checksummed append-only log of complete snapshots. Snapshot
  JSON uses a closed, versioned schema and deterministic object-key ordering.
- A frame is authoritative only when its metadata checksum, payload checksum,
  and final commit marker all validate. An incomplete final frame is ignored
  during reads and truncated by the next successful mutation. Corrupt
  committed frames and unknown file, frame, operation, or snapshot versions
  fail closed.
- Mutations take a root lock and a bounded striped run lock. The locks combine
  in-process exclusion with operating-system byte-range locks, so cooperating
  processes cannot use last-write-wins. Revision CAS and fencing still decide
  which owner may publish.
- The payload and commit marker are flushed with `Flush(true)` before success
  is acknowledged. `UseWriteThrough` adds `FileOptions.WriteThrough`; it does
  not replace the explicit flush.
- If acknowledgement is lost after the final flush, callers reopen or read
  the run and retry against its persisted revision and stage status. A retry
  may observe `AlreadyExists`, `RevisionConflict`, or
  `AlreadyRequested`; it must not infer that the prior mutation was absent.
- Run count, operation count, snapshot bytes, frame bytes, per-run file bytes,
  root bytes, restored stage instances, and lock wait time are bounded through
  `FileWorkflowRunStoreOptions`. Capacity is checked before an append, so a
  rejected mutation does not become authoritative.

The file store's durability evidence is a successfully validated frame after
the operating system reports a completed disk flush. It is intended for one
machine and a local filesystem whose locking and flush contracts are honored.
It does not claim distributed consensus, safe use on network filesystems, or
survival from storage hardware that falsely acknowledges flushes. Portable
.NET also cannot provide a directory-fsync guarantee for a newly created
file, so applications needing that power-loss boundary should place the root
on a filesystem/platform with documented create-and-fsync semantics and
validate it operationally.

The current format appends a full snapshot for every mutation and does not
compact logs. Configure operation and byte limits for the expected heartbeat
and checkpoint volume, then rotate or archive completed run roots outside an
active store. Every concurrently running scheduler must use a unique owner
identifier.

`WorkflowAgentStepExecutor` is the optional adapter for
`IDurableAgentRuntime`. Its nested run identity is derived from the stable
workflow stage instance, so recovery resumes the same durable agent run.
Game-specific request construction and outcome projection remain behind
`IWorkflowAgentRunAdapter`.

An adapter may additionally implement
`IWorkflowAgentTerminalOutcomeProjector` for game-defined optional branches.
The default remains fail-closed, and a projected fallback passes through the
same stage output-schema validation before downstream work can consume it.
