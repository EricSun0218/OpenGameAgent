# Changelog

## Unreleased

- Add `QueuedGameActionHandler`, a bounded engine-thread action handoff with FIFO pumping,
  queued cancellation, active-action settlement, shutdown cleanup, and main-thread recovery over
  the existing durable action journal and receipt protocol.

## 0.3.0-alpha.2

- Add durable image input for game observations: bounded PNG/JPEG/WebP/GIF decode admission, immutable content-addressed local objects, reference-only transcripts, provider/model preflight before reads, tool-result image persistence, JSON/SSE transport, and owner-authorized retrieval.
- Document the recommended large-world perception stack: bounded structured state, sparse BEV/topological summaries, selective screenshots, exact query tools, and deterministic game-owned execution rather than raw voxel dumps.
- Add the optional `TaskPlanExtension` for session/actor-scoped persistent ordered checklists, revision-checked mutations, host-validated evidence, per-input advancement guards, pending-work routing, typed UI projection events, and bounded terminal retention.
- Add typed, model-free host queries for persisted goals and task plans, including session revisions, and scope goal-change events with their session/actor key and input ID.
- Add batched, payload-free mailbox pending-status queries that distinguish ready work from active leases without claiming delivery or incrementing attempts.
- Add durable task-plan pause/resume with revision checks, preserved in-progress steps, non-runnable paused routing, typed change reasons, and restart coverage.
- Add the optional `OpenGameAgent.Memory` package with a model-agnostic embedding provider contract, authoritative-save verification, rebuildable local vector indexes, hybrid lexical/vector recall, structured diagnostics, and game-time-aware reranking.
- Add deterministic authoritative memory snapshots for in-memory and local-file stores so derived indexes can be rebuilt explicitly after embedding model or preprocessing changes.
- Document local source references and game-provided local embedding integration, including BGE-M3-compatible query/document adapters and save boundaries.
- Make generated memory, delegation, structured-interaction, large-result artifact, external-knowledge artifact, and MCP artifact IDs stable across fresh runtime attempts, and keep engine project lock files aligned with the release version.

## 0.3.0-alpha.1

- Introduce a compact stateful streaming Agent kernel with typed content, validated tools, steering, follow-up, hooks, cancellation, transcript integrity checks, bounded concurrency, and explicit failure results.
- Add safe tool execution with schema validation, source-ordered results, progress, model/tool deadlines, conflict-key serialization, and fail-closed uncertain-write semantics across a tool batch.
- Add the game runtime with arbitrary structured inputs, floating-point preservation, named game timelines, automatic quick/full/workflow routing, optimistic sessions, duplicate protection, live steering/abort, and per-actor concurrency.
- Add a typed extension API with immutable composition, namespaced session state, lifecycle events, channels, diagnostics, and official policy, searchable-tool, interaction, goal, memory, artifact, knowledge, delegation, tracing, and durable workflow-graph extensions.
- Add durable action intents and receipts, prepared/dispatched/final recovery, resumable sequential and dependency-graph workflows, game-time memory and expiry, recursive skills, recurring schedules, actor mailboxes, context-window admission, large-result artifact spill, and media-generation API contracts.
- Add crash-tolerant, cross-process-coordinated local file stores for sessions, action journals, workflow checkpoints, memories, mailboxes, artifacts, delegations, and hot-reloaded directory skills, with identity and saved-state trust checks.
- Add capability-aware provider/model catalogs, reasoning and cost metadata, dynamic refresh, replaceable authentication, and developer-hosted short-lived credentials.
- Add an executable bundled model directory with provider-specific dispatch, compatibility flags, request transforms, cost tiers, response observation, and nine native wire APIs.
- Add optional bounded browser/device authentication flows, explicit client registration, stored credential refresh, and cancellation-safe login settlement.
- Add lazy external tool-server search/describe/call by default with explicit direct exposure for small trusted catalogs.
- Add optional Agent Plugins 1.0.0 package loading with portable Skill and MCP discovery, client namespaces, path containment, placeholder expansion, and component-level failure isolation.
- Add native Anthropic, Bedrock, Google Gemini/Vertex, Mistral, OpenAI Responses/Azure, OpenAI-compatible, remote-proxy, and message-gateway providers with cross-provider transcript handoff.
- Add a provider-neutral image/audio/video registry, strict generic HTTP media jobs, dedicated image generation with progressive previews, and typed partial tool output.
- Add bounded request/response parsing, rotating credentials, safe response metadata observation, protocol-aware retries, and retry/fallback composition that stops before replaying meaningful streamed output.
- Add append-only branch/lane session history, bounded search and projection, usage accounting, cross-process mutation safety, prompt templates, richer skill diagnostics, and context-overflow recovery.
- Add Godot 4.7 .NET and Unity 6 adapters with local and remote modes, bounded main-thread delivery with terminal reservation, package verification, and real local-runtime editor tests on Windows.
- Add an optional .NET 8 JSON/SSE server, engine-compatible client, authenticated steering and abort, bounded JSON request bodies, strict wire contracts, and redirect/credential guidance.
- Add bilingual documentation, a buildable living-world action example, pinned release automation, and cross-platform .NET validation.
