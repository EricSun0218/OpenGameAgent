# Architecture

OpenGameAgent has two deliberately small layers and a set of optional extensions and adapters. The kernel contracts are designed to stabilize early: product features belong in extensions or game code unless they are required to make every model/tool loop correct.

## Layers

### `OpenGameAgent.Kernel`

The kernel owns one stateful model/tool loop:

1. validate and append caller messages;
2. prepare a bounded model request;
3. publish streaming events while assembling the assistant message;
4. validate every tool call and produce a result for every accepted call;
5. execute tools sequentially or with bounded, conflict-aware concurrency;
6. append tool results in source order;
7. apply steering or follow-up messages;
8. continue until the model stops, a hook stops the run, cancellation occurs, or a limit is reached.

It knows nothing about NPCs, worlds, inventories, or engines. Its canonical values are typed content parts (`text`, `json`, `resource`, durable `image_attachment`, `reasoning`, and `tool_call`), messages, model requests, tools, and events. Inline image bytes are request-boundary input; canonical history stores only immutable attachment references.

`Agent` owns mutable transcript and queue state. `AgentLoop` is the lower-level execution function. A host that already owns state can call the loop directly; most integrations should keep an `Agent` or use `GameAgentRuntime`.

Canonical transcripts are validated as a protocol, not just as independent messages. Every assistant tool call must have one matching tool result before the next non-tool message, tool names must agree, pending assistant responses cannot be persisted, and unresolved calls are rejected. `AgentValidation.ValidateTranscript` exposes the same preflight for importers and custom stores.

Streaming updates and canonical history are separate. A message-update wire event contains the new delta for low-latency presentation; the message-ended event contains the complete assembled assistant message. Hosts may shed intermediate presentation events when a bounded engine queue is full, but they reserve terminal delivery and persist only complete canonical messages.

### `OpenGameAgent`

The game layer converts a `GameInput` into a bounded kernel run. It owns:

- game coordinates (`session`, `actor`, `timeline`, `tick`);
- context and skill selection;
- quick/full/workflow routing;
- optimistic session persistence and duplicate input detection;
- same-actor ordering and bounded cross-actor concurrency;
- reusable action, workflow, memory, schedule, and mailbox primitives.

It does not own a universal world model. Context remains opaque JSON supplied by the game, so a turn-based strategy game and a real-time character simulation can use the same runtime without flattening their data into a common schema.

### Optional packages

- `OpenGameAgent.Extensions` adds policy, searchable tools, structured player interaction, goals, host-verified task plans, memory, artifacts, external knowledge, delegation, tracing, and durable workflow graphs.
- `OpenGameAgent.Memory` adds an optional, model-agnostic embedding contract, rebuildable vector index, lexical/vector hybrid recall, structured diagnostics, and game-time reranking. It never replaces the authoritative memory save.
- `OpenGameAgent.Models` adds provider/model catalogs, capability-aware selection, reasoning levels, cost metadata, dynamic refresh, and replaceable authentication.
- `OpenGameAgent.Models.BuiltIn` turns the bundled directory into an executable multi-provider model runtime; `OpenGameAgent.Models.Auth.BuiltIn` adds explicitly configured browser and device authorization flows.
- `OpenGameAgent.ProviderTransport` centralizes bounded response observations, header guards, and retry metadata without adding HTTP concepts to the kernel.
- `OpenGameAgent.Attachments` defines immutable image references and storage admission; `OpenGameAgent.Attachments.Local` provides a content-addressed local implementation with real decode and integrity checks.
- `OpenGameAgent.Media` routes image, audio, and video generation by provider/model capability while keeping generation jobs outside the text/tool protocol.
- `OpenGameAgent.Connectors.Mcp` exposes external tool servers through one lazy, searchable tool by default. Direct tool exposure is an explicit opt-in.
- Provider, persistence, engine, client, and server packages stay replaceable and do not change kernel semantics.

`GameAgentBuilder` is the composition root. Extensions register prompt fragments, context, tools, skills, route rules, workflows, hooks, model providers, services, typed lifecycle events, and typed channels. Registration names are scoped and validated, extension state is namespaced inside the session, and the builder is one-shot so a running configuration cannot be mutated accidentally.

This separation is the compatibility boundary. The kernel owns canonical messages, streaming, turns, tools, cancellation, steering, and transcript correctness. The game runtime owns game coordinates, actor lanes, route admission, and persistence orchestration. Everything more specialized should normally remain an optional extension.

## Authority boundary

Model output is a proposal. A tool handler is an adapter into game business code. Only that code can decide that a mutation committed.

For state-changing tools, use this sequence:

```text
model tool call
  -> JSON Schema validation
  -> GameActionIntent reserved in journal
  -> game handler validates rules and expected revision
  -> game state transaction
  -> GameActionReceipt stored
  -> receipt returned to model
```

The default versioned operation identity is derived from the session, actor, stable game input ID, action, timeline/tick, optional save generation, model turn, and tool-call source index. It therefore remains stable when the same logical call is replayed, but cannot collide across actors, sessions, actions, or save generations. A game can replace this with a semantic identity through `GameActionOperationIdFactory`. Replaying an already closed operation returns the stored receipt, while changed arguments or authority preconditions at the same identity fail closed.

The journal distinguishes `Prepared`, `Dispatched`, and a final receipt. If a process can fail after dispatch but before the receipt is recorded, `RecoverAsync` asks the game to reconcile the operation. The framework reports `Uncertain` when the game cannot prove the outcome; it never converts cancellation or a timeout into permission to repeat a write.

Read-only and idempotent tools may use the kernel directly. Non-idempotent state changes should use `DurableGameActionDispatcher` with a persistent journal in production.

## Time

`GameMoment` is not wall-clock time. It contains a timeline ID, signed 64-bit tick, and optional calendar JSON. The game chooses what one tick means. A memory or trigger can therefore follow turns, days, months, eras, combat frames, or a custom calendar.

Timeline IDs make save forks and simulations explicit. Moments from different timelines cannot be ordered. Operational leases in mailboxes use real duration because they protect concurrent workers; narrative memory and scheduling use game time.

## Concurrency

`GameAgentRuntime` uses one logical lane per `(session, actor)`:

- inputs for the same actor execute in order;
- different actors can execute concurrently up to `MaxConcurrentActors`;
- per-actor queues are bounded;
- session saves use expected revisions to detect conflicting writers.

Inside one model turn, `SafeParallel` executes compatible tool calls concurrently. Read-only calls can overlap. Write calls sharing a conflict key are serialized. Results are appended in model source order, so completion timing does not scramble the transcript.

For durable game actions, the same resolved conflict key is copied into `GameActionIntent`. Official in-memory and file journals implement `IGameActionConflictJournal`, so matching writes are also serialized across different actors, sessions, and model runs while model inference remains concurrent. The durable scope is `(timelineId, generationId, conflictKey)`. A final committed, rejected, or failed receipt releases the scope for the next claimant. An uncertain dispatched action keeps the scope blocked until authoritative reconciliation records a final receipt; caller cancellation cannot release it. The current contract intentionally supports one key per action. Hosts that need several resources should derive one canonical composite key or serialize the mutation in their authoritative transaction.

Existing `IGameActionJournal` implementations remain usable for actions whose `ConflictKey` is null. A conflict-bearing action fails closed unless the configured journal also implements `IGameActionConflictJournal`; it never silently falls back to process-local locking.

Large worlds should not invoke every NPC on every frame. Let deterministic game simulation decide which actors need inference, then enqueue those actors. `GameTimeScheduler`, `GameSignal`, and `IGameMailbox` are building blocks for this admission layer; they are not a hidden global simulation policy. `GameTimeScheduler.CaptureState()` provides a saveable recurring-trigger position so loading a game does not replay already emitted occurrences.

## Context, memory, and skills

`IGameContextProvider` supplies current authoritative context slices. Memory is intentionally separate: `IGameMemoryStore` stores and filters records, while game code decides which retrieved memories become a context slice. This avoids silently inserting stale or private memory.

The included memory stores support scopes, kinds, tags, importance, owner, game-time cutoffs, and expiry. `RankedGameMemoryStore` applies a game-selected ranker. The optional `OpenGameAgent.Memory` package adds model-agnostic vector indexing and hybrid recall while keeping the original store authoritative; a game supplies its local or remote embedding implementation and explicitly rebuilds after changing its model identity.

Skills are bounded instruction packages selected by input type and required tools. Skills do not install or execute code. Directory-backed skills accept either a zero-configuration `SKILL.md` with scalar `name` and `description` front matter, or `skill.json` plus a separate Markdown instruction file for game-specific filtering. Manifests are rescanned for each selection and only selected instruction files are loaded, allowing safe edits without rebuilding the runtime.

Transcript compaction is also a provider-view operation. The included summarizing compactor keeps complete conversational suffixes and never splits a tool exchange. If no complete suffix fits the requested target, it summarizes the whole prior transcript into one canonical summary message. Games that need tokenizer-aware or domain-specific summaries can replace the compactor.

Context admission runs before the first request, after tool turns, and again after final request hooks. A hook therefore cannot accidentally bypass the configured context window. Large text or JSON tool results can be moved into the artifact store and replaced with a bounded handle and preview. This keeps canonical results recoverable without repeatedly paying their full context cost.

Image admission follows the same canonical/request-view split. Inline user or tool-result images are fully validated and persisted before they enter session history. The active provider/model is preflighted, then immutable references are resolved into bytes only for the outgoing model request. System and assistant images are rejected; generated assets use the media pipeline. See [Image input and game perception](image-input.md).

The system prompt keeps the most reusable bytes first: base instructions, then selected skills, then mutable authoritative game context. This ordering preserves the longest possible provider-cache prefix when world state changes, without moving dynamic state out of the game-owned context boundary.

After a tool turn, `GameAgentRuntime` refreshes authoritative context, tools, and selected skills by default before the next model request. A configured next-turn hook can supply an explicit replacement context instead. Active game-layer runs can also be steered or aborted by `GameSessionKey`; messages never cross actor lanes.

## Routing

The route is selected before skills and the kernel run:

- `QuickResponse`: one model turn, no tools;
- `Agent`: bounded multi-turn model/tool loop;
- `Workflow`: a named deterministic or hybrid workflow.

Explicit input metadata has highest precedence. Then typed routes, an optional classifier, and a conservative structural fallback are applied. If tools or pending work exist, the fallback chooses the full agent route. Games can replace `IGameRoutePolicy` entirely.

## Placement

The shared projects target `netstandard2.1`. They can run:

- in a Godot .NET process;
- in a Unity process;
- in an existing C# game server;
- behind the included .NET 8 JSON/SSE host.

Godot and Unity adapters only bridge lifecycle, cancellation, JSON, signals/events, and main-thread callback delivery. They do not fork the runtime semantics. A remote engine client sends the same `GameInput` representation to the service.

Provider credentials can be supplied directly, resolved from a game-owned credential store, or obtained as short-lived tokens from a developer-hosted gateway. The framework never claims that a secret embedded in a shipped client is protected.

## Failure model

- Model transport errors become terminal run results.
- Retry and fallback providers may switch attempts only before meaningful streamed content or usage is exposed, preventing a visible partial response or charged request from being replayed silently.
- Invalid or truncated tool calls do not execute.
- Every accepted tool call receives a bounded tool result, including validation and timeout failures.
- Tool timeouts do not wait forever for a non-cooperative implementation.
- Subscriber failures are isolated, recorded, and cause that subscriber to be removed.
- Session revision conflicts are explicit results.
- Custom stores must return the exact state they claim to have saved; mismatched session snapshots, checkpoints, action entries, or receipts fail closed.
- Local stores write through temporary files and replace the durable target. Processes using the same directory coordinate with cross-process file leases. They are still local save-store building blocks, not a distributed database; multi-host services need transactional shared storage and actor ownership.
- Bounded limits protect strings, JSON, messages, turns, tokens, queues, tools, callbacks, progress, and concurrency.
- Durable workflow checkpoints bind the workflow, session, actor, and canonical input. The same interrupted input can resume; a different input is rejected until the unfinished invocation is settled.
- The optional HTTP service accepts JSON only on mutation endpoints, bounds request bodies to 8 MB by default, and parses with a fixed depth limit.

The framework cannot make arbitrary game code transactional. The game must make mutation handlers idempotent or recoverable at the operation-ID boundary.

Workflow checkpoints and game-state commits are also separate transactions unless the host supplies a shared transactional implementation. Every workflow node that can cause a side effect should use a stable operation ID and the durable action dispatcher. When several save forks remain accessible in one store, assign a new session/save namespace as well as a new timeline ID; transcript identity is `(session, actor)`.

The built-in schema validator intentionally implements a common bounded subset: type, enum/const, object properties and required fields, additional properties, arrays, strings, and numeric bounds. The final tool definitions produced by request hooks are preflighted before a provider stream is opened, and returned arguments are validated again before execution. Unsupported assertion keywords fail closed even when nested in an unselected schema branch. For advanced validation, give the tool a permissive `{}` schema and supply its custom validation delegate; mutation handlers must still revalidate business rules.
