# @opengameagent/memory

Optional structured game-time memory, bounded hybrid recall, derived-index rebuild, and embedding contracts. Authoritative records remain in a `MemoryAuthority`; vector indexes are disposable accelerators.

Recall results include ordered, content-free stage metrics for the work that actually ran. Each metric reports only the
stage (`partition`, `lexical`, `index-restore`, `embedding`, `vector`, `fusion`, `hydrate`, or `rerank`), outcome, duration, and input/output counts.
Optional stages are absent when they do not run. Embedding or vector failures marked `failed-open` appear only when the
caller selected lexical-only degradation; cancellation and hard failure reject without returning a partial metric result.

## Disposable index preparation

Every recall first reads `MemoryAuthority.readPartitionState(selector)`. The authority returns a durable index-partition
revision and the active count for the selected owner/timeline/generation/actor/session/scope lane. A zero count returns
only the `partition` metric and `vectorStatus: "proven-empty"` when vectors are configured. It performs no lexical search,
query embedding, vector search, scan, or background preparation, including on the first request after restart. Counts are
conservative with respect to viewer, tags, kind and game-tick filters; normal recall still applies those filters.

`GameMemory.rebuild(partition)` prepares one owner/timeline/generation vector partition. An index implementing optional
`restore`/`checkpoint` capabilities first tries the authority revision's completed snapshot; unchanged snapshots return
`{ indexed: 0, embedded: 0, restored: count }` without scanning records or invoking the embedding provider. A missing,
changed, or invalid snapshot rebuilds only that partition, reusing validated persisted embeddings, and reports
`{ indexed, embedded, restored: 0 }`. The completed scan's count and durable revision must still match before checkpointing.
The `index-restore` recall metric counts one attempted partition and zero or one restored partition.

Memory mutations invalidate affected readiness. Direct authority API writes also change its durable revision, which recall
checks before using cached readiness. Hosts using foreground preparation must explicitly rebuild affected partitions;
background mode schedules them under its configured admission policy. Raw SQL writes outside the authority are unsupported.

A host that cannot block its first game interaction on rebuild may configure `indexPreparation` with explicit
`maxPendingPartitions`, `maxConcurrentPartitions`, and `retryFailure` values. Recall for an unprepared partition then runs
lexical search, fusion, hydration, and any reranker immediately, without query embedding or vector search, while one shared
background rebuild is scheduled. The result reports `preparing`, `unprepared`, or `preparation-failed` as appropriate.
After successful preparation, later recall reports `used` or `proven-empty`. `retryFailure: "next-recall"` retries only
when a later recall arrives; `"never"` leaves the partition failed for that preparation lifecycle. There are no implicit
capacities, timers, polling loops, or retry delays.

Preparation state and same-partition work are coordinated across `GameMemory` instances that share the same
`MemoryVectorIndex` object. Those instances must share the same `MemoryAuthority` object and use the same
`maxTrackedIndexPartitions`, preparation mode, and background configuration. Capacity overflow stays lexical-only and
starts no untracked work. An explicit rebuild waits for same-partition background work rather than racing another index
replacement. Caller cancellation does not cancel shared work.

At shutdown, call `closeIndexPreparation("drain")` to stop admission and await queued work, or
`closeIndexPreparation("abort")` to also abort active and queued work before awaiting settlement. Call it once per shared
vector-index lifecycle before closing its authority or index. A closed `GameMemory` no longer schedules cold partitions;
a new instance may start a new lifecycle after close resolves.

## Integrated recall benchmark

Build the workspace, then run either explicit acceptance profile:

```sh
npm run build
npm run benchmark:memory -- --config scripts/benchmark-profiles/memory-recall-10k.json
npm run benchmark:memory -- --config scripts/benchmark-profiles/memory-recall-100k.json
```

The checked-in `10k × 1024` and `100k × 1024` profiles are deterministic acceptance workloads, not product limits,
runtime defaults, or model recommendations. The 100k profile keeps the validated 10k rebuild and embedding batch working
set so the comparison changes data scale without also increasing one-call vector allocation. Both use synthetic embeddings,
so the benchmark does not require BGE-M3 or any other specific embedding model. Custom profiles must explicitly provide
every numeric setting and a corresponding evidence note; the script has no numeric benchmark defaults.

A benchmark that completes measurement writes the admitted configuration, host and package environment, database bytes,
seed, reopen and rebuild durations, content-free per-stage median/P95/maximum duration and count summaries, Recall@K,
indexed/embedded counts, restart equivalence, and invariant outcomes. This complete report is retained when Recall@K misses
its configured acceptance minimum. Earlier configuration, restart-equivalence, or forbidden-scan failures return only a
typed failure envelope because the report is incomplete. Measurements for a named workload and host do not establish an
SLA or an automatic pass/fail latency threshold.
