# Changelog

## 0.3.0-alpha.1

- Introduce a compact stateful streaming Agent kernel with typed content, validated tools, steering, follow-up, hooks, cancellation, transcript integrity checks, bounded concurrency, and explicit failure results.
- Add safe tool execution with schema validation, source-ordered results, progress, timeout handling, conflict-key serialization, and fail-closed uncertain-write semantics across a tool batch.
- Add the game runtime with arbitrary structured inputs, floating-point preservation, named game timelines, automatic quick/full/workflow routing, optimistic sessions, duplicate protection, live steering/abort, and per-actor concurrency.
- Add durable action intents and receipts, prepared/dispatched/final recovery, resumable workflows, game-time memory and expiry, skills, recurring schedules, actor mailboxes, transcript compaction, and media-generation API contracts.
- Add crash-tolerant single-process file stores for sessions, action journals, workflow checkpoints, memories, mailboxes, and hot-reloaded directory skills, with identity and saved-state trust checks.
- Add strict streaming OpenAI-compatible and generic HTTP media providers, bounded request/response parsing, rotating credentials, polling controls, and retry/fallback provider composition.
- Add Godot 4.7 .NET and Unity 6 adapters with local and remote modes, bounded main-thread delivery with terminal reservation, package verification, and real local-runtime editor tests on Windows.
- Add an optional .NET 8 JSON/SSE server, engine-compatible client, authenticated steering and abort, strict wire contracts, and redirect/credential guidance.
- Add bilingual documentation, a buildable living-world action example, pinned release automation, and cross-platform .NET validation.
