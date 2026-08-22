# OpenGameAgent requirement ledger

This file is the durable, source-independent ledger for accepted OpenGameAgent framework work. It deliberately excludes game-specific rules and private Cloud control-plane implementation.

## Workflow

1. Add every user request or cross-thread delegation to the active plan.
2. Audit existing source, tests, documentation, and artifacts before accepting a gap.
3. Before design or implementation, classify a confirmed gap as open-source, closed-source, or mixed and record the decision and rationale.
4. Record an accepted open-source capability below before implementation. Forward a closed-source capability to the user-designated closed-framework task instead of implementing it here. Split a mixed capability into a minimal public, self-hostable contract and a separately handed-off managed implementation.
5. Close an item only after focused tests, all applicable release gates, documentation, commit, push, and source-thread notification. A handoff is closed here only after the target task has received the context, ownership boundary, and acceptance criteria.

## Ownership classification

| Classification | Belongs here | Routing rule |
|---|---|---|
| Open-source | Reusable and replaceable runtime behavior, SDKs, versioned wire contracts, provider-neutral extension points, security primitives, and conformance fixtures that any game host can self-host | Implement and verify in this repository |
| Closed-source | Hosted control planes, organization or tenant administration, managed identity and secrets, quotas, billing, private registries, managed observability or storage, commercial scheduling or high availability, SLAs, and proprietary hosted optimization | Forward to the separate closed-framework task; do not implement here |
| Mixed | A public interoperability boundary plus a managed commercial implementation | Keep only the smallest independently self-hostable contract and conformance surface here; forward the managed portion |

Classification is mandatory for every new requirement, including cross-thread delegations. If later evidence changes the boundary, update the active plan and this ledger rather than silently moving work between repositories.

## Active requirements

| Priority | Capability | Status | Completion evidence |
| --- | --- | --- | --- |
| P0 | Crash-safe ordinary-tool dispatch with stable operation IDs and explicit `Never` / `Safe` / `Recoverable` replay policy | Completed | `IGameRunOperationJournal`, in-memory/file stores, runtime execution hook, restart/corruption tests, architecture docs, full release gates |
| P1 | Restart-resumable delegation with lineage, lease/reclaim, continuation, bounded descendant listing, reports, inherited authority, and duplicate prevention | Completed | `AgentDelegationExtension.ResumePendingAsync`, opaque persisted requests, renewable CAS leases, owner-scoped lineage, tests, docs, full release gates |
| P1 | Provider-neutral visual observation projection with immutable originals, derived transforms, request budgets, deterministic selection, structured scene/BEV context, caching, and provenance | Completed | Projection contracts, Skia request projector, model-request integration, immutable derived objects, projection events/tests/docs, full Release/package/engine gates |
| P1 | Reconstructable model-visible context provenance for context, memory, skills, tools, compaction, images, route, provider/model, and artifacts | Completed | Private in-memory/file stores, request/response records, redaction, restart/corruption tests/docs, full Release/package/engine gates |
| P1 | Runtime health snapshots for providers, MCP, local endpoints, realtime, and media with declared/available/ready/degraded/unavailable states | Completed | Typed bounded monitor, in-process host, protected server/client projection, aggregation/timeout/auth tests/docs, full Release/package/engine gates |
| P1 | Extension development kit: scaffold, manifest/permission validation, conformance fixtures, fake runtime, dependency diagnostics, and editor-only reload boundary | Completed | Strict manifest/SemVer/permission/dependency validation, real-runtime fake-provider conformance runner, buildable scaffold, bilingual docs, lifecycle race stress test, full Release/package/engine gates |
| P1 | Host-owned task-plan advancement for authoritative receipts without exposing mechanical progress decisions to the model | Completed | Optional model-tool removal, committed-input/revision/evidence/CAS guards, typed host result, tests and integration docs |
| P1 | Composable local speech pipeline that connects bounded VAD/STT, the existing agent runtime, and streaming TTS without a second agent loop | Completed | Provider-neutral VAD/STT/TTS contracts, composable realtime transport, OpenAI-compatible local STT/TTS adapters, barge-in/limits/runtime bridge tests, full Release/package/engine gates |
| P2 | Exact-repeat tool-loop detection with host-configurable exemptions and bounded advisory/termination policy | Completed | Deep-canonical prepared-call identity, transparent polling exemptions, bounded advisory/termination, typed events, metrics, docs, full Release/package/engine gates |
| P2 | Ordered tool-execution epochs so independent reads remain parallel around sequential barriers without weakening conflict or durable-write safety | Completed | Deterministic source-order epochs, sequential barriers, cancellation, conflict/uncertain-write safety, saturation tests, full Release/package/engine gates |
| P2 | Provider and extension conformance kits plus generated TypeScript/Python Runtime Protocol SDKs | Completed | Bounded model-stream/cancellation/secrecy runner, deterministic schema-derived clients/reducers, strict JSON/cursor guards, packed clean consumers, bilingual docs, full Release/package/engine gates |
| P2 | Local model lifecycle developer tools for explicit inventory, warmup, load/unload, and host-authorized acquisition hooks | Completed | Provider-neutral lifecycle contract/manager, fail-closed acquisition authorizer, bounded concurrency/timeout/progress, explicit Ollama backend/tests/docs, full Release/package/engine gates |

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
