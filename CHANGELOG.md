# Changelog

## 0.1.0-alpha.1

- Added the versioned typed game-agent protocol.
- Added the durable streaming agent loop, controls, budgets, and recovery.
- Added stateless completion and durable Direct, Agent, and Workflow routing
  with bounded custom policies and least-capability deterministic fallback.
- Added per-operation reasoning, sampling, prompt-cache, and ordered provider
  route controls with durable recovery.
- Added strict tool validation, conflict-aware scheduling, skills, context, and
  local baseline memory.
- Added a crash-tolerant file memory store with restart recovery, bounded
  deterministic search, corruption detection, and optimistic revisions.
- Added optional bounded BM25 memory search without requiring an embedding
  model, plus policy-driven recall, writeback, provenance, and lifecycle
  integration in the agent loop.
- Added bounded memory query transformers, result rerankers, and a game-clock
  aware importance, recency, and diversity reranker.
- Added atomic mixed upsert/delete memory batches for both in-memory and
  file-backed stores, including whole-frame crash recovery and bounded input.
- Added fail-closed conditional memory writes and deletes so records cannot be
  overwritten or removed across world, save, scope, timeline/epoch,
  perspective, entity incarnation, game-clock, or stale-record boundaries;
  runtime-managed bare batch upserts are create-only.
- Added a public, versioned authority-aware memory-store contract for runtime
  outboxes and version 2 file frames that preserve version 1 replay semantics.
- Added an explicit legacy prepared-outbox replay bridge so a crash between the
  old prepare and completion events remains recoverable after upgrading; new
  commits continue to use authority-aware mutation admission.
- Documented the one-way memory write boundary: reading version 1 is
  non-mutating, while the first version 2 append requires the current reader
  thereafter; back up or copy the file first when rollback is required.
- Added policy-scoped deferred-tool search, exact durable activation, recovery,
  and same-turn required-tool admission for active skills.
- Aligned runtime `ToolDescriptor` validation with the schema enum and timeout
  bounds; retry declarations remain metadata and do not automatically replay
  host actions in this alpha.
- Added a streaming chat-completions provider with DeepSeek V4 Pro defaults.
- Added pre-snapshot provider input budgets, bounded adapter expansion, and a
  hard encoded HTTP request-body limit.
- Added durable provider-route identities and separate stable-prefix/dynamic
  prompt digests for cache and recovery diagnostics.
- Added bounded canonical semantic digests and fail-closed durable resume
  guards for stale game coordinates, including multi-actor and engine surfaces.
- Versioned composite semantic digests with typed, length-delimited fields so
  JSON boundaries, field types, and null presence cannot alias.
- Added a durable per-turn side-effect call policy that rejects an over-limit
  response before write-ahead while preserving valid pure reads.
- Added an optional entity-incarnation fence for restricted observations,
  including durable context, controls, host receipts, and Godot/Unity bridges.
- Added fail-fast aggregate token, action, duration, and cost reservations for
  multi-actor batches, recorded in each admitted batch manifest.
- Added a repeatable 64-actor coordination allocation and latency smoke gate.
- Added bounded, paged presentation-chunk replay with explicit expired and
  ahead cursor outcomes for reconnecting engine UI consumers.
- Restricted pluggable conversation compactors to audited, low-authority
  summary data; the runtime owns the normalized envelope and verifies every
  referenced source-message ID.
- Added a replaceable bounded conversation-context engine and typed required or
  optional lifecycle middleware around runs, model dispatch, and tool batches.
- Restricted custom context-engine output to byte-identical admitted messages
  or one validated low-authority historical-summary envelope; arbitrary
  synthetic user, assistant, and tool messages fail closed.
- Resume admission middleware now runs after durable recovery with the
  runtime-validated agent, world, explicit session, and game-context
  coordinate, before any ownership, provider, reconciler, or host work.
- Added durable group interactions, membership and incarnation fences,
  private/shared memory boundaries, and bounded multi-actor lifecycle
  coordination.
- Added a deterministic durable workflow kernel with sequence, bounded
  foreach fan-out, parallel execution, bounded loops, reduction, cancellation,
  and restart recovery.
- Added workflow-wide deadlines that fence late non-cooperative executors, plus
  bounded isolation and shutdown for every game-owned Agent-step callback.
- Reserved workflow cancellation ownership before executor start, retained
  execution tokens until detached work actually finishes, and disposed
  deadline/heartbeat timers on every terminal path.
- Added a fail-closed, game-owned terminal-outcome projection hook for
  explicitly optional Agent workflow branches; projected values still pass
  the declared workflow schema.
- Added an Anthropic Messages streaming provider and provider capability,
  workload, cache, continuation, and transport conformance contracts.
- Added explicit Anthropic `none`, manual-budget, and adaptive thinking-route
  declarations so model-specific reasoning controls are never guessed from a
  model name.
- Added runtime metrics, redacted traces, replay, scenario evaluation, and
  performance smoke gates.
- Added invocation-wide immutable execution-policy leases so hot reload applies
  only to new agent-loop invocations and cannot change tools, skills, routes,
  or model identity midway through a decision.
- Added Godot and Unity engine packages.
- Added bounded child Agent supervision with durable lineage, nested depth and
  concurrency limits, cancellation propagation, and failure-isolated batches
  across the shared, Godot, and Unity surfaces.
- Coalesced repeated child and Godot request cancellation through bounded
  dispatchers, retaining capacity until game-owned cancellation callbacks
  actually finish.
- Isolated child caller, timeout, parent, and shutdown cancellation from
  runtime callbacks so a blocking child cannot own the triggering thread.
- Hardened shutdown ownership across routed policies, child runs, stateless
  completions, detached provider cleanup, custom context engines, and
  lifecycle middleware so owned transports and stores are never released
  while callbacks can still use them.
- Added isolated Godot addon-consumer compilation and runtime gates; packaged
  adapter sources depend only on public runtime APIs and the default verifier
  now targets the default release artifact.
- Hardened every Godot run ingress with a bounded, cancellable deep snapshot
  outside the lifecycle lock while still reserving active-run capacity first.
- Moved GDScript child-run, resume, routed, completion, and multi-actor batch
  mapping behind fail-fast operation admission so saturated hosts do not parse
  large Variant graphs on the engine thread.
- Added GDScript routed execution and stateless completion, and separated
  request-cancellation capacity from lifecycle cancellation so the documented
  active-run range cannot be capped by the lifecycle queue.
- Made the Godot Variant bridge reject non-finite or lossy JSON numbers with a
  stable mapping error instead of silently rounding them, and verified the
  packaged sources with implicit usings disabled.
- Preserved game-authored integral Floats, negative zero, and large finite
  Floats while recovering only known protocol integer fields parsed by Godot;
  aligned GDScript completion admission with the 4,096-message Core boundary.
- Bounded routed-execution shutdown callbacks process-wide, with retriable
  async cancellation leases that do not make an idle or naturally drained
  router wait behind an unrelated blocked callback.
- Compared workflow numeric schema bounds as exact JSON numbers, including
  scientific values outside the CLR decimal range.
- Added a reserved Unity terminal-observer queue and identity-rich fault events
  so saturated main-thread action traffic cannot hide a terminal run outcome.
- Reserved per-run Unity cancellation ownership with separate normal and
  shutdown lanes, keeping caller cancellation off the Unity thread and
  promoting queued ordinary cancellation so saturation cannot starve shutdown.
- Snapshotted mutable Unity custom-backend requests after admission and before
  dispatch, with pre-serialization structural limits and owned nested state,
  without taking validation authority away from the injected backend; ordinary
  runtime-event and application-pause subscribers are isolated too.
- Verified the licensed Unity 6000.5.6f1 Windows matrix through EditMode,
  PlayMode, Mono Player, and IL2CPP Player build-and-run gates, including the
  two-turn durable tool-loop marker scenario.
- Verified the DeepSeek V4 Pro live-provider path through streaming, one
  authoritative host tool call, usage accounting, and clean completion without
  persisting the local credential in repository artifacts.
- Added persistent Agent identities, graph edges, bounded mailboxes and
  residency, with deterministic eviction and separate execution/model capacity.
- Added session-bound model-visible context deltas, cited memory distillation,
  durable external attention, game-time trigger catch-up/overlap policies, and
  idempotent hierarchical budget charges.
- Added a closed declarative extension catalog that binds only game-owned
  registries and never loads executable payloads.
- Added safe model-authored command plans with ordered/parallel DAG execution,
  bounded foreach, reduce and feedback loops, durable host receipts, and
  external-attention recovery.
- Added provider-neutral image, video, speech, and structured-content jobs,
  local or remote HTTP providers, streaming speech, allowlisted content-addressed
  artifacts, and recoverable host validation/commit transactions.
- Added typed generation APIs and engine event surfaces for Godot and Unity.
- Added crash-safe provider-dispatch fencing and recoverable artifact
  materialization checkpoints, including synchronous jobs without provider IDs.
- Hardened generated-plan foreach consumers, game-trigger coalescing, and
  monotonic streaming-speech lifecycle validation.
