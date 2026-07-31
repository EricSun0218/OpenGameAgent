# Changelog

## 0.1.0-alpha.1

- Added the versioned typed game-agent protocol.
- Added the durable streaming agent loop, controls, budgets, and recovery.
- Added strict tool validation, conflict-aware scheduling, skills, context, and
  local baseline memory.
- Added a crash-tolerant file memory store with restart recovery, bounded
  deterministic search, corruption detection, and optimistic revisions.
- Added optional bounded BM25 memory search without requiring an embedding
  model, plus policy-driven recall, writeback, provenance, and lifecycle
  integration in the agent loop.
- Added atomic mixed upsert/delete memory batches for both in-memory and
  file-backed stores, including whole-frame crash recovery and bounded input.
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
- Added durable group interactions, membership and incarnation fences,
  private/shared memory boundaries, and bounded multi-actor lifecycle
  coordination.
- Added a deterministic durable workflow kernel with sequence, parallel,
  conditional, bounded loop, wait, reduce, cancellation, and restart recovery.
- Added an Anthropic Messages streaming provider and provider capability,
  workload, cache, continuation, and transport conformance contracts.
- Added runtime metrics, redacted traces, replay, scenario evaluation, and
  performance smoke gates.
- Added invocation-wide immutable execution-policy leases so hot reload applies
  only to new agent-loop invocations and cannot change tools, skills, routes,
  or model identity midway through a decision.
- Added Godot and Unity engine packages.
