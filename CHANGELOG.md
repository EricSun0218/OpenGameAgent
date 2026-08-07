# Changelog

## 0.3.0-alpha.1

- Introduce a compact stateful streaming Agent kernel with typed content, validated tools, steering, follow-up, hooks, cancellation, transcript integrity checks, bounded concurrency, and explicit failure results.
- Add safe tool execution with schema validation, source-ordered results, progress, model/tool deadlines, conflict-key serialization, and fail-closed uncertain-write semantics across a tool batch.
- Add the game runtime with arbitrary structured inputs, floating-point preservation, named game timelines, automatic quick/full/workflow routing, optimistic sessions, duplicate protection, live steering/abort, and per-actor concurrency.
- Add a typed extension API with immutable composition, namespaced session state, lifecycle events, channels, diagnostics, and official policy, searchable-tool, interaction, goal, memory, artifact, knowledge, delegation, tracing, and durable workflow-graph extensions.
- Add durable action intents and receipts, prepared/dispatched/final recovery, resumable sequential and dependency-graph workflows, game-time memory and expiry, recursive skills, recurring schedules, actor mailboxes, context-window admission, large-result artifact spill, and media-generation API contracts.
- Add crash-tolerant, cross-process-coordinated local file stores for sessions, action journals, workflow checkpoints, memories, mailboxes, artifacts, delegations, and hot-reloaded directory skills, with identity and saved-state trust checks.
- Add capability-aware provider/model catalogs, reasoning and cost metadata, dynamic refresh, replaceable authentication, and developer-hosted short-lived credentials.
- Add lazy external tool-server search/describe/call by default with explicit direct exposure for small trusted catalogs.
- Add strict streaming OpenAI-compatible and generic HTTP media providers, bounded request/response parsing, rotating credentials, polling controls, and retry/fallback composition that stops before replaying meaningful streamed output.
- Add Godot 4.7 .NET and Unity 6 adapters with local and remote modes, bounded main-thread delivery with terminal reservation, package verification, and real local-runtime editor tests on Windows.
- Add an optional .NET 8 JSON/SSE server, engine-compatible client, authenticated steering and abort, bounded JSON request bodies, strict wire contracts, and redirect/credential guidance.
- Add bilingual documentation, a buildable living-world action example, pinned release automation, and cross-platform .NET validation.
