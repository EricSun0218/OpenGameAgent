# @opengameagent/runtime

Actor lifecycle and canonical event-store composition for OpenGameAgent.

`start()` and `submit()` capture the input identity, authority and detached JSON payload when called, before waiting
for admission. Control, cancellation and deferred-resume requests similarly retain their original target. Caller changes
after a call cannot redirect that request. Host ID and clock callbacks cannot bypass queue capacity or replace a nested
run; already reserved durable admissions still drain through the existing shutdown boundary.

Both input entry points enforce `maxInputBytes` on the canonical UTF-8 JSON payload and `maxInputDepth` on that
payload's value depth. A budget failure is a `RuntimeInputLimitError` with `code: "input_too_large" | "input_too_deep"`,
the corresponding configured `limit`, and the underlying JSON error as its cause. `start()` throws synchronously;
`submit()` rejects its promise. Rejection happens before run creation, queue reservation, persistence, or provider
invocation. No canonical run event exists for this unadmitted input; the caller can correct it and reuse its input ID.

`replaceActor(options)` replaces only the in-process activation for the exact `sessionId` and `actorId` in `options`.
The existing actor must be registered and fully idle: it cannot have an admitting, queued, cancelling or active run, a
pending or abandoning deferred operation, another registration/replacement, or closed Runtime admission. The Runtime
restores and stages the replacement without exposing it, rechecks the same activation under the scheduler boundary,
then swaps it atomically. New starts and durable submissions for that stream are rejected while staging is in progress.
The next admitted run uses the replacement model, instructions, Tools and context configuration.

Canonical events, durable transcript and external Action, approval and memory authorities remain keyed by the stable
stream and are not deleted or rewritten. Existing Runtime stream listeners, including their unsubscribe closures, remain
attached across the swap; activation-owned Agent listeners are detached when an old or failed staged activation is
released. A failed replacement leaves the old activation registered. The package intentionally provides no standalone
actor unregister, batch replacement, product reload policy or consumer cleanup callback.

Inputs may carry `admissionAuthority: { scope, generation }`. `closeAuthority({ sessionId, actorId, scope, generation,
operationId })` first persists a permanent fence for that exact authority, then cancels and drains its local queued,
active, and deferred work. It preserves the actor activation, stable stream, transcript, persona, and unrelated scopes
or generations. A later generation can use the same stream without replacing or retiring its actor. Input idempotency
includes the authority identity, so reusing an input ID with a different authority conflicts even when its payload matches.

`readAuthorityClosure(key)` reads the durable fence without requiring actor registration. Retrying the same close request
returns the current durable phase across process restart; a conflicting operation identity or payload rejects. Admission and the
canonical `run.started` / `run.resumed` append reject a closed authority before provider activation. Runtime also checks
the fence before and after the host Tool guard and again after durable Tool-start recording, immediately before the final
local execution check. These checks do not roll back external effects that already started.

Closure waits for local deferred abandonment and its canonical terminal event, while remote provider cancellation may
still be pending. A local cancellation or persistence failure rejects the close call while its durable fence remains
queryable and effective. The closure record represents that fence; it is not proof that every past external effect was
undone or every retained historical run is settled. `start()` keeps its synchronous handle API and reports a rejected
durable enqueue through failed completion; `submit()` rejects with `RunAuthorityClosedError`.

A close may additionally carry `workRetirement: { retirementId, partition, recordedAt }`, where `partition` requires
all five fields `{ ownerId, timelineId, generation, sessionId, actorId }`. Its session and actor must match the closed
run stream; the host explicitly maps its timeline generation to the run admission scope and generation. No wildcard
or implicit timeline-to-scope mapping is applied. Bind `SqliteEventStore` to both its Schedule and Mailbox stores and
configure `GameRuntimeOptions.drainWorkAuthority(request)` before using composite work retirement.

The first transaction permanently fences the exact work lane and run authority, optionally retires Goals, cancels
future schedules and unleased pending work, and retains every pending lease token as evidence. Runtime then drains its
matching runs, awaits the host's `drainWorkAuthority`, and commits the final cancellation and lease release in a second
transaction. The callback must stop and await detached producers, Schedule dispatchers, persistent handoffs and handlers
that Runtime does not track. It waits for execution to become quiescent, not for fenced `complete`/`abandon` calls to
succeed. Lease expiry alone is not evidence of quiescence. The callback must also reconcile any host work recovered after
a crash before returning; a no-op is appropriate only when the host can prove it has no such work.
Wait for the host's tracked operations directly; awaiting Runtime `waitForIdle()` from this callback would also wait
for the current close operation itself.

Reads expose `workRetirementResult.phase: "fenced" | "finalized"` and canonical decimal string counts
`cancelledScheduleCount`, `cancelledOccurrenceCount`, and `cancelledMessageCount`. A failed drain or final commit leaves
`fenced` durable and effective; retry the identical complete close request to resume. Only `finalized` proves the host
drain boundary was acknowledged and leases were terminalized. Completed handoffs remain completed, pending work becomes
`cancelled`, and effects already committed are not undone. Final results remain stable after history is purged.
The in-memory event store rejects composite work retirement before changing any run or Goal state; standalone in-memory
Schedule and Mailbox retirement only provides process-local semantics.

`retireActor(stream, reason)` is the atomic per-actor lifecycle boundary for unloading a world, save, or generation. It
fences new admission for that exact stream, aborts every active, admitting, and queued run, and waits for local execution
to stop before releasing the idle activation and its listeners. A queued run is retired without invoking its
model or Tools. Concurrent retirement calls share one operation; a completed repeat returns `false`, and registering the
same stream later creates a fresh activation from retained durable authorities. A pending deferred provider operation or
another registration/replacement transition fails closed. Retirement does not delete transcripts, canonical events,
Actions, approvals, goals, memory, or game-owned state.
Success requires a persisted canonical terminal event for each drained run. If a terminal write fails, retirement
rejects with an `AggregateError` containing `ActorRetirementPersistenceError` even though the stopped activation can be
released. Retained canonical authority remains unsettled; registering the actor again and submitting the same input uses
the existing orphan-reconciliation path without replaying its provider or fabricating the missing terminal event during
retirement. If cancellation itself cannot be persisted and execution remains live, the actor stays registered and its
ready queue resumes after the retirement fence is removed. Deferred resumption respects the same admission fence;
pending deferred authority is preserved when retirement rejects as busy.

Runtime `run.metrics` events extend Agent segment durations with `admission` (ScheduledRun creation until `run.queued`
is durably appended) and `queue` (that commit until activation, or until a queued run is cancelled). Both are microsecond
decimal strings and are included in `native` and `total`; `firstResponse` and `firstTool`, when present, include the same
waiting time. Agent context, provider, durable durations, and attempt/Tool counts retain their original meaning.
Each deferred resumption starts a fresh scheduling segment with zero admission time; suspended wall time and actor
registration are excluded. `GameRuntimeOptions.metricsNow` optionally supplies finite monotonic milliseconds for these
measurements and defaults to `performance.now`; its elapsed durations can be combined with the Agent's independent
monotonic clock. This starts at ScheduledRun creation, so pre-creation submission validation and historical lookup are
excluded.

A queued cancellation or failure before an Agent starts emits one `run.metrics` event with zero provider/Tool counts
before its real terminal event. These writes share the event serialization boundary. If metrics cannot be persisted,
Runtime preserves the unsettled canonical authority instead of fabricating terminal settlement or provider counts.

A durable SQLite host uses one exclusively owned `SqliteAuthority` for its event store and every optional SQLite store.
The host owns process lifecycle: it stops network and in-process admission, aborts or drains its tracked run handles,
closes the listener, and only then closes that authority. Runtime orphan reconciliation therefore runs only after the
replacement process has acquired database ownership; persisted event history alone is not treated as proof that another
runtime is dead.

`stopAdmission()` permanently rejects new actor registration or replacement, new prompts, durable submissions, and
deferred resumes.
After that exact boundary, `waitForIdle()` waits for in-flight actor restoration, durable submission admission, scoped
authority closure, queued runs, and active runs; calling it while admission is open rejects so a transient empty scheduler
cannot be used as a shutdown fence. Existing runs continue normally unless the host calls `abortAll(reason)`. That idempotent operation stops
admission, prevents queued providers from starting, attempts every queued and active abort, and waits for the same idle
boundary. A terminal-persistence failure is returned as an `AggregateError` after sibling aborts have still been
attempted. Pending deferred provider operations remain explicit resume/abandon state and are not silently abandoned.
These methods do not close HTTP/SSE connections or the host-owned SQLite authority and add no timeout or exit policy.

`GameRuntime` keeps a durable provider response suspended until the host explicitly resumes or abandons its current
operation. A resume admits exactly one provider fetch. The operation identifier names the current pending state; it is
not a network retry key, because the same provider operation can remain deferred and be explicitly fetched again.

`submit()` and `resumeDeferred()` return a required `eventCursor` whose value is the canonical stream revision
immediately before that run's first `run.queued` event. The cursor is stable for equivalent durable retries, restored
terminal results, and deferred resumes. Pass it as `afterRevision` when beginning run observation; subsequent SSE
reconnects continue from the last delivered event ID.

A completed `GameRunResult` includes `finalOutput` when the run produced a non-empty assistant text message. It is the
last such message in that exact run, assembled from canonical assistant text parts without reasoning summaries. The
same value is reconstructed from the canonical event authority after restart. Deferred, aborted and failed results do
not expose it; a completed run that produced no text leaves it absent.

Canonical event retention is likewise host driven. `CanonicalEventStore.purgeTerminalRuns` can remove only a complete
oldest prefix made entirely of terminal runs, strictly before the host cutoff, within the explicit event-count batch,
and outside `retainRunIds`. It never cuts through a run or deletes an event followed by a later event for the same run.
The stream head is preserved after physical deletion, so later appends remain monotonic and any consumer cursor behind
the retained boundary receives an explicit transcript-reconciliation gap—even when no event rows remain. For full actor
retirement, call `retireActor` before purging durable authorities in host-chosen order, while retaining every run still
referenced by transcripts, Actions, approvals, delegation, audit, or game state.
Every purge request requires `retainBudget: { maxCount, maxBytes }` to bound the complete `retainRunIds` list before it is
copied or used for deletion. Both values are host-selected non-negative safe integers; bytes are the sum of UTF-8 run ID
bytes. An over-budget list rejects the operation without deleting events. An empty list may use a zero count/byte budget;
never omit a live reference to make maintenance fit its budget.

`control(coordinate, command)` is fenced by the complete session, actor, run and current-turn coordinate. For a host that
owns an exact run but must cancel it across Pi Tool continuations, `abortRun({ sessionId, actorId, runId }, reason)`
atomically selects that run's current turn. It can also remove an admitted or queued run, never follows a different run
ID, and never serializes the optional in-process reason into canonical events. Once `control.accepted` is durable, a
queued run is made non-executable before its terminal event is appended; a terminal persistence failure settles the
local handle as failed and still releases queue capacity.

`subscribeImmediate(stream, listener)` is a narrow synchronous projection seam. It runs after the canonical append and
revision update but before `appendEvent` returns. Each listener receives a deeply frozen snapshot, exceptions and
rejected async returns are isolated, and returned promises are never awaited. Use ordinary `subscribe` for asynchronous
I/O. Immediate listeners are observers only and cannot replace event-store authority.

`BoundedAsyncQueue` is a single-consumer in-process handoff. Its positive host-supplied capacity bounds admitted pending
values; overflow closes the queue while preserving the admitted prefix, and `undefined` is reserved as the closed-read
sentinel. It is coordination machinery, not durable storage or a multi-consumer broadcast.

## Scheduler acceptance benchmark

The checked-in `runtime-scheduler-256x32` profile is a reproducible P2 acceptance workload, not a runtime default. It
admits 32 runs into each of 256 actor lanes, verifies the exact 8-actor global concurrency bound, checks actor-local FIFO
and complete round-robin service, and proves that one additional synchronous admission is rejected at the derived 8192
waiting-run capacity. The synthetic provider performs no network I/O, so measured timings characterize scheduler and
canonical in-memory event overhead on the reported machine rather than model-provider latency.

Every admission, provider-start, completion, and handle-settlement wait uses the profile's explicit no-progress
deadline. The timer resets on each matching progress event; timeout and unexpected-error reports retain their phase,
counters, event totals, environment, Git state, and captured memory snapshots.

Build the workspace before running the benchmark because the runner imports the same compiled package surfaces that a
host consumes:

```sh
npm run build
npm run benchmark:runtime -- --config scripts/benchmark-profiles/runtime-scheduler-256x32.json --output .benchmark-data/runtime-scheduler-256x32.json
```
