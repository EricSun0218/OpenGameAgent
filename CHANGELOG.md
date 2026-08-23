# Changelog

## Unreleased

- Remove the unused standalone immutable session-history subsystem; the runtime transcript remains the
  canonical conversation record, while products can keep separate host-owned audit or trace storage.
- Centralize shared .NET test-project and xUnit configuration, and refresh locked dependencies to the
  current package version so every test project follows one release-tested configuration.
- Share bounded HTTP/SSE transport validation and parsing across the stock server and Runtime Protocol
  clients without changing their public contracts.
- Add a provider-neutral model-stream conformance runner with standard request fixtures, bounded
  lifecycle/terminal checks, cancellation probes, resolved-identity checks, and sensitive-diagnostic guards.
- Generate dependency-free TypeScript and standard-library Python Runtime Protocol v1 clients and
  reducers from the normative schema, with locked builds, packed clean consumers, and exact safe-integer cursors.
- Execute compatible tools in ordered parallel epochs around sequential barriers, retaining source-order
  transcripts, bounded concurrency, conflict-key serialization, uncertain-write blocking, and cancellation safety.
- Detect deep-canonicalized exact prepared tool-call loops across turns, with host-configurable polling
  exemptions, bounded advisory/termination thresholds, typed events, wire projection, and performance metrics.
- Ship the Runtime Protocol dependency in the Unity package and make real-editor smoke tests wait only for
  the editor process, rather than persistent compiler-server descendants.
- Add provider-neutral composable local speech using bounded VAD, transcription, and streaming speech
  synthesis through the existing realtime bridge, plus OpenAI-compatible local STT/TTS adapters.
- Add explicit, host-authorized local model inventory, warmup, load, unload, and acquisition lifecycle
  contracts with an Ollama backend; agent runs never trigger implicit downloads.
- Let hosts advance durable task plans from committed authoritative evidence while optionally removing
  model-visible advancement, preserving revision, once-per-input, evidence, retention, and CAS guards.
- Make delegated-agent shutdown reliably signal handles even when cancellation races active-handle
  publication, while leaving uncooperative work recoverable after restart.

## 0.3.0-alpha.4

- Add OpenGameAgent Runtime Protocol v1 as optional `OpenGameAgent.Runtime.Protocol`,
  `OpenGameAgent.Runtime.Hosting`, and `OpenGameAgent.Client` packages, with capability negotiation,
  canonical run/turn/item events, bounded cursor replay, gap reconciliation, exact run/turn control,
  conformance fixtures, JSON Schema, and dependency-free C++ DTOs.
- Let hosts keep automatic Quick/short-Agent routing while withholding persistent planning through a
  host-derived execution scope; unauthorized actors cannot collect planning tools or upgrade into a
  durable plan.
- Add the optional `OpenGameAgent.Memory.Onnx` package for bounded, offline BGE-M3 INT8 inference from
  a host-supplied local model directory, including SentencePiece tokenization, normalized pooling,
  query/document modes, manifest verification, batching, cancellation, and memory diagnostics.
- Make automatic route classification parse bounded structured replies across OpenAI-compatible
  providers, distinguish empty, reasoning-only, invalid, timeout, and provider failures, and apply
  DeepSeek-compatible token/thinking request shapes without exposing hidden reasoning.
- Add a durable generated-asset lifecycle that stages generated media, validates host import receipts,
  binds assets to authoritative save coordinates, and recovers safely after cancellation or restart.
- Publish tested Unity packages through immutable GitHub UPM tags and OpenUPM, and prepare Unity and
  Godot store listings from the same public source tree.
- Add a machine-readable release manifest that binds every release to its source commit, Runtime
  Protocol version, package set, asset sizes, and frozen SHA-256 hashes.
- Make Runtime SSE publication and audience-projection source registration atomic so a concurrent
  reader cannot miss a terminal event under load.

- Add the optional `OpenGameAgent.Providers.Local` package with bounded discovery and health checks
  for Ollama, LM Studio, LocalAI, llama.cpp, and vLLM; keyless loopback realtime presets for LocalAI
  and Speaches; OpenAI-compatible local embeddings; LocalAI image/video/TTS generation; and
  host-authored, source-aware ComfyUI workflows with targeted job cancellation.
- Preserve interleaved commentary, reasoning, tool, and final-answer stream blocks and their text
  phases through the OpenAI Responses adapter, while public audience projection continues to hide
  private reasoning.
- Add provider-neutral, host-brokered high-risk tool approval with disabled, explicit-only,
  confirm-once, and task-scoped modes; final post-rewrite authorization; one-time credentials bound
  to canonical arguments and authoritative save revision; crash-safe storage; owner-authorized remote
  endpoints; and approval-wait performance attribution.
- Add input-aware tool visibility policies that filter collected tool definitions before every model
  request, plus independent visibility predicates for the memory append and search tools.
- Add official OpenAI Images and Volcengine Ark/Seedream image providers with bounded reference-image
  inputs, credential isolation, validated image outputs, and model-registry integration.
- Add an authorized, cursor-paged durable transcript read API for runtimes, the stock server, and remote
  clients, including attachment metadata without inline binary payloads and bounded UTF-8 responses.
- Add a native Unreal Engine 5.8 remote adapter with JSON/SSE control, game-thread event dispatch, bounded
  response streaming, source packaging, and real-editor automation coverage.
- Carry tool conflict keys into durable action intents and journals, and atomically coordinate matching
  writes across actors, sessions, runs, and process restarts within a timeline/save generation. An
  uncertain dispatch blocks the key until authoritative reconciliation; unrelated keys and generations
  remain concurrent, and legacy journals continue to serve actions without conflict keys.
- Add optional provider-neutral realtime conversations with bounded PCM16 queues, live transcript and
  audio events, barge-in cancellation/truncation, non-blocking background-agent handoff and steering,
  200 ms streamed-output forwarding, and cancel-replace presentation behavior channels.
- Add an OpenAI Realtime WebSocket adapter with bounded wire parsing, credential-only handshake
  headers, transcript/handoff/behavior mapping, and remote plaintext rejection.
- Add an optional Volcengine realtime speech adapter with duplex PCM16 input/output, VAD and
  transcription handoff, word-timed subtitles, cancellable streaming TTS sub-sessions, bounded
  backpressure, handshake-only credentials, per-session NPC voices, and durable-action-safe bridge
  integration.
- Make realtime stop and disposal share one bounded lifecycle operation so explicit stop, remote
  closure, asynchronous disposal, and concurrent cleanup cannot double-dispose provider resources.
- Add the optional `OpenGameAgent.DevTools` package and CLI for bounded append-only JSONL trace
  recording, crash-tail recovery, observation-only HTML playback, run summaries, and strict offline
  evaluation suitable for local debugging and CI gates.
- Enrich the tracing extension with complete model-usage, resolved-provider, response, and persisted
  per-cause usage-ledger metadata without recording prompts, tool arguments, credentials, or reasoning
  text by default.
- Document bounded NPC adaptation as an opt-in host policy built from immutable memories, skills,
  receipts, traces, and evaluations; generated proposals never gain authority to modify code,
  permissions, game rules, or world state directly.
- Change the project license to MIT and align the Unity package, citation metadata, contribution terms,
  README attribution guidance, and package metadata. Redistributions must retain the copyright and MIT
  license notice; displaying the project logo is not required.
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
