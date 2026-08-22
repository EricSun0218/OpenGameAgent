# OpenGameAgent requirement ledger

This file is the durable, source-independent ledger for accepted OpenGameAgent framework work. It deliberately excludes game-specific rules and private Cloud control-plane implementation.

## Workflow

1. Add every user request or cross-thread delegation to the active plan.
2. Audit existing source, tests, documentation, and artifacts before accepting a gap.
3. Record the accepted generic capability below before implementation.
4. Close an item only after focused tests, all applicable release gates, documentation, commit, push, and source-thread notification.

## Active requirements

| Priority | Capability | Status | Completion evidence |
| --- | --- | --- | --- |
| P0 | Crash-safe ordinary-tool dispatch with stable operation IDs and explicit `Never` / `Safe` / `Recoverable` replay policy | Implemented; gates pending | `IGameRunOperationJournal`, in-memory/file stores, runtime execution hook, restart/corruption tests, architecture docs |
| P1 | Restart-resumable delegation with lineage, lease/reclaim, continuation, bounded descendant listing, reports, inherited authority, and duplicate prevention | Planned | Persistence and restart tests, public API, docs, full release gates |
| P1 | Provider-neutral visual observation projection with immutable originals, derived transforms, request budgets, deterministic selection, structured scene/BEV context, caching, and provenance | Planned | Projection tests, model-request integration, diagnostics, docs, full release gates |
| P1 | Reconstructable model-visible context provenance for context, memory, skills, tools, compaction, images, route, provider/model, and artifacts | Planned | Trace/history schema, redaction tests, replay/eval integration, docs |
| P1 | Runtime health snapshots for providers, MCP, local endpoints, realtime, and media with declared/available/ready/degraded/unavailable states | Planned | Typed API, server/client projection, bounded diagnostics, tests, docs |
| P1 | Extension development kit: scaffold, manifest/permission validation, conformance fixtures, fake runtime, dependency diagnostics, and editor-only reload boundary | Planned | Template/tooling tests, package gates, docs |
| P1 | Composable local speech pipeline that connects bounded VAD/STT, the existing agent runtime, and streaming TTS without a second agent loop | Planned | Provider-neutral contracts, barge-in/backpressure tests, local adapter examples, docs |
| P2 | Exact-repeat tool-loop detection with host-configurable exemptions and bounded advisory/termination policy | Planned | Loop/retry/legitimate-poll tests, metrics, docs |
| P2 | Ordered tool-execution epochs so independent reads remain parallel around sequential barriers without weakening conflict or durable-write safety | Planned | Deterministic ordering, barrier, cancellation, and saturation tests |
| P2 | Provider and extension conformance kits plus generated TypeScript/Python Runtime Protocol SDKs | Planned | Deterministic generation, fixtures, clean consumers, docs |
| P2 | Local model lifecycle developer tools for explicit inventory, warmup, load/unload, and host-authorized acquisition hooks | Planned | No implicit downloads, bounded progress/cancel tests, docs |

## Completed foundations

- Runtime Protocol v1 with Session/Run/Turn/Item events, capability negotiation, stable event IDs, replay/gap reconciliation, exact run/turn control, C# client, schemas/fixtures, and C++ DTOs.
- Local endpoint discovery and health profiles for Ollama, LM Studio, LocalAI, llama.cpp, and vLLM; LocalAI/Speaches realtime presets; LocalAI and trusted ComfyUI media adapters.
- Optional in-process BGE-M3 INT8 ONNX embedding provider.
- Host-derived persistent-planning authority scopes.
- Durable game actions, receipts, conflict coordination, high-risk approvals, adaptive routing, image attachments, realtime speech, generated media/assets, trace, benchmark, and evaluation foundations.

## Superseded, rejected, or paused

| Item | Status | Rationale |
| --- | --- | --- |
| Name the protocol after another framework's Harness v2 | Superseded | The public boundary is OpenGameAgent Runtime Protocol v1; implementation ideas may be studied, but names and code are original. |
| Official PostgreSQL/multi-region commercial implementation in the open-source repository | Superseded | Open source owns replaceable contracts and conformance; hosted databases, distributed scheduling, tenancy, billing, and disaster recovery belong to the separate Cloud product. |
| Runtime loading of model-generated self-modifying code | Rejected | It violates the game's authority and extension safety boundary. Signed, host-installed extensions remain supported. |
| ACP bridge without a demonstrated consumer | Rejected for current scope | It adds a second agent protocol without improving the supported game-engine paths. Re-open only with a concrete interoperability consumer and acceptance tests. |
| NuGet.org alpha.4 and matching GitHub Release publication | Paused | The user explicitly paused this path until the NuGet trusted-publishing Create-page bug is fixed. Source references and UPM/OpenUPM remain valid delivery paths. |
| Persist and replay steer/follow-up messages against a replacement run after process restart | Rejected | Exact control is intentionally bound to the live `runId`/turn. Replaying it against a new run would violate the coordinate contract; reconnecting clients reconcile transcript/terminal state and issue a new follow-up. |
| Add a second generic reducer for model attempts, compaction, or usage settlement | Superseded | Model attempts are retriable before canonical commit; compaction and usage already settle through durable session CAS and idempotent usage record IDs. The missing safety boundary was ordinary tool dispatch, now covered without duplicating those stores. |
