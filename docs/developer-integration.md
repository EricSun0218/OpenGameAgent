# Developer integration boundary

OpenGameAgent is infrastructure for a game team to build characters inside its own game, using project-owned persona
data and authoritative game state.

```text
game-owned state and rules
        ↕ authoritative Actions
Unity / Godot / Unreal thin client
        ↕ authenticated Runtime Protocol
TypeScript sidecar: Runtime → one Pi Agent loop → configured model provider
        ↕
shared SQLite authority and optional local resource stores
```

The game remains authoritative. Model text can propose behavior and Tools can prepare durable Actions, but only the game
adapter can recheck current authority and commit a world mutation.

## Project-owned character behavior

Define persona and worldbook data in the game's source-controlled configuration and compile it with
`@opengameagent/characters`.

- Persona `must` and `mustNot` rules are non-negotiable instructions. Examples are illustrative and deliberately weaker.
- The host supplies the current game snapshot scanned by the worldbook. Worldbook selection is read-only and bounded by
  the host's exact byte, token, match, activation, and recursion budgets.
- Current game context and confirmed Tool results outrank descriptive persona content. State-changing behavior must use
  admitted Tools and reliable Actions.
- The compiled persona fingerprint and source/revision lineage should be retained with provider provenance so a game team
  can reconstruct which definition produced a request.

`@opengameagent/png` is only a bounded PNG/APNG binary-envelope inspector shared by image/resource packages. It is not a
character-card format or an import path.

## Sidecar composition order

1. Select the exact Node.js and npm versions declared by the repository and install from the frozen lockfile.
2. Compile and validate the game's persona, worldbook, Tool catalog, authorization policy, and every resource limit before
   opening external admission.
3. Open one file-backed `SqliteAuthority` with `ownership: "exclusive"` for a durable Runtime. Select `busyTimeoutMs` and
   `verifyOnOpen` from the host's operational policy; the framework does not guess them.
4. Construct `SqliteEventStore`, the optional transcript store, and every enabled SQLite feature store from that same
   authority. Do not give each feature its own database connection.
5. Construct one `GameRuntime` with explicit actor concurrency, queued-run, input-byte, and JSON-depth limits.
6. Choose credential resolution deliberately. Ambient-only games can retain Pi's environment/platform resolution. A
   Windows desktop or self-hosted sidecar that needs persistent referenced API keys or OAuth tokens can open
   `@opengameagent/credentials-windows` with an existing protected application-data directory and explicit entry,
   plaintext-byte, ciphertext-file, JSON-depth, parent-directory scan, lock-wait, and lock-retry budgets, then inject that
   `CredentialStore` as `credentialReferences`. Provider selection, credential-entry UI, profile activation, and release
   policy remain game responsibilities.
7. Register each fixed game actor with its Pi model route, provider stream, compiled base instructions, authorized context
   sources, Tool sources, transcript binding, and optional provider-provenance binding. When a game-owned configuration
   revision changes for an existing exact session/actor stream, call `replaceActor(options)` only after that actor is
   idle. The Runtime stages and restores the replacement, preserves stream listeners and durable authorities, and makes
   the next run use the new options. A busy, deferred, registering, replacing, missing or stopping stream fails instead
   of mutating the live activation; the game retains identity validation and the decision to retry later.
8. Construct `createRuntimeServer()` with a game-owned authentication/authorization controller and explicit HTTP, SSE,
   paging, Action-claim, and polling limits. Bind it through `serveRuntimeServer()` only after actor registration succeeds.
9. Configure the engine client with the same actor/session selectors and independently derived request, response,
   attachment, timeout, queue, execution, and finite retry-schedule budgets.

For optional long-lived memory, either explicitly rebuild each exact owner/timeline/generation vector partition before
semantic recall or enable `GameMemory.indexPreparation` with host-selected pending, concurrent, and retry limits. In the
background mode, a cold first recall remains lexical-only and schedules one bounded shared rebuild; later recall uses the
ready index. Every `GameMemory` sharing one `MemoryVectorIndex` object must also share one `MemoryAuthority` object and use
the same explicit tracking capacity, preparation mode, and preparation configuration. Do not add a game-side readiness map or
mutate the memory authority around `GameMemory`; an out-of-band authority change requires a rebuild.
`MemoryContextSource.observeRecall` can forward the exact content-free vector status and stage metrics to host telemetry
without another recall; the observer is non-blocking and cannot alter model context success.

The in-memory stores and fake providers are test/conformance tools. A durable local or self-hosted game Runtime uses the
shared SQLite authority.

## Continuous local voice

Keep one `GameRealtimeVoiceService` as the speech-to-Runtime-to-speech authority. For a continuous microphone session,
compose `GameContinuousLocalSpeechSession` with `VoiceActivityUtteranceCapture.openSession()` and give each detected
utterance a new exact `(sessionId, actorId, voiceSessionId, epoch, utteranceId)` coordinate. The next confirmed speech
edge interrupts only the preceding coordinate before it enters the same registered Pi actor. Do not add a game-side
conversation Agent, Tool loop, transcript authority, or coordinate-agnostic stop path.

The game owns microphone permissions, device lifetime, its bounded PCM ring/transport, playback, and any explicit
resampling to mono PCM16 16 kHz. It also supplies every endpointing probability, frame-count, byte, pending-turn, and
session limit. Stop or cancellation must close the continuous handle and then release the physical capture lease.

For local Silero endpointing, construct `SileroVoiceActivityDetector` with a game-installed ONNX path, its exact
lowercase SHA-256, bounded path/model sizes, model-load and inference deadlines, and maximum concurrent sessions. Feed
exact 1024-byte frames. The Adapter owns per-stream recurrent state, but it neither downloads a model nor opens a device,
resamples, plays audio, or chooses thresholds. Model distribution and license compliance remain the game's release
responsibility.

## Durable manual speech assets

Keep conversational reply audio in `GameRealtimeVoiceService`; it is intentionally ephemeral. For an explicit workflow
that creates a reusable NPC voice asset, register `createRealtimeSpeechMediaProvider()` as one Adapter in the existing
`MediaJobService` and submit an `audio` request with exactly one text field. The host fixes the synthesizer, voice profile,
PCM16 output format, and all text/chunk/audio limits when constructing the Adapter. A successful attempt publishes one
`audio/wav` result through the existing immutable resource store and result-bundle selection path.

Do not add a second TTS job manager, provider selector, retry loop, filename authority, playback path, or game-state
write. The game imports a selected immutable result through its ordinary authorized Action boundary. An interrupted
`submitting` attempt whose provider outcome cannot be proved is explicitly reconciled to `outcome_unknown` without a
provider call; abandon it before requesting a new service attempt and never replay the old submission key.

Windows CurrentUser DPAPI normally binds ciphertext to the same user profile and machine. It protects credentials at
rest but does not sandbox malicious code already running as that user. Administrative password reset, user-profile loss,
or machine migration can make data unrecoverable. The optional Adapter fails closed in those cases; it never switches to
plaintext or machine-wide protection. See the package README for its filesystem identity, atomic replacement, process
lock, cancellation, and stable-error contracts.

## Runtime and Action flow

- Give every game input a stable `inputId`. Repeating the same ID and content returns the durable result; reusing it with
  different content fails as a conflict.
- Consume canonical events from the last fully applied revision. A retention gap requires transcript/game-state
  reconciliation rather than an invented continuation.
- Treat an Action delivery as either `dispatch` or `reconcile`. A reconcile delivery inspects game authority and never
  executes the Action again.
- Perform a world write only through the engine adapter's short-lived `TryCommit` boundary, including the final authority
  recheck, operation deduplication, mutation, and resulting evidence in one game transaction.
- After an ambiguous transport result, read Runtime authority before deciding whether another explicit command is safe.
  Do not turn transport retry into repeated game execution.
- Engine hosts should use the official exact `RuntimeRunSession` when they need events, Actions, Approvals, terminal
  convergence and abort for one accepted run. Its callback deadlines, retry policies, polling interval and page size are
  explicit host inputs. Action and Approval workers start only after the exact `run.queued` admission event is validated,
  and untrusted events, Action deliveries and Approval records are rejected before game callbacks. Do not consume the
  same run's low-level streams in parallel with the session.
- Persist the exact run request and acceptance before crash recovery. On Godot C#, attach that accepted run instead of
  submitting it again, and drain actor-scoped reconciliation through the read-only recovery callback before Action
  dispatch resumes.
- Retention is scheduled by the game host with explicit cutoffs, batch sizes, and protected run/transcript revisions.
  Every purge request includes `retainBudget: { maxCount, maxBytes }` for the complete protection list: non-negative safe
  integers bounding entry count and summed UTF-8 identifier bytes. Budget overflow rejects purge before copying or deletion;
  keep every live reference and increase host capacity or postpone cleanup instead of truncating the list.
  Purge only terminal Action/Approval state, complete terminal canonical-run prefixes, and transcript prefixes covered
  by an exact checkpoint; no framework TTL discovers product retention policy on the host's behalf.

## Shutdown and crash recovery

Process lifecycle remains host authority. `GameRuntime` does not close the network listener or SQLite connection for the
host. A normal shutdown uses this order:

1. Stop new engine/network requests and call `runtime.stopAdmission()`. New actor registration or replacement, run
   submission, and deferred resume now fail; reads, exact run control, and explicit deferred abandonment remain
   available.
2. For a drain policy, await `runtime.waitForIdle()`. For a cancellation policy, await `runtime.abortAll(reason)`, which
   stops admission, prevents queued runs from starting, attempts every active/queued abort, and then waits for idle.
3. Resolve or explicitly abandon any suspended provider operation according to host policy. A suspended operation is not
   an executing run and is not implicitly abandoned by `abortAll`.
4. Close the Node listener and its active SSE connections according to host transport policy. Node `server.close()` stops
   new connections and drains ordinary in-flight requests; a host choosing forceful connection teardown calls
   `server.closeAllConnections()` after initiating `close()`.
5. Stop each optional memory index-preparation lifecycle with one selected policy and await either
   `closeIndexPreparation("drain")` or `closeIndexPreparation("abort")` before closing its vector index or memory
   authority.
6. Close the shared `SqliteAuthority` only after the Runtime idle boundary, listener shutdown, and optional memory
   preparation shutdown complete.

`waitForIdle()` deliberately rejects while admission is open, preventing a transient empty queue from being mistaken for
a safe database-close boundary. The idle fence also includes actor restoration and durable submissions that entered
before closure but have not created a run yet. `abortAll()` is idempotent; a failure to persist one terminal abort is
reported as an aggregate after all sibling aborts have been attempted. The host must not close SQLite after that failure
until its own recovery policy establishes safety. No timeout or process-exit policy is supplied by the framework.

After an abrupt exit, retain the database and its hot `-wal` file together under their original names. A replacement
exclusive authority performs SQLite recovery and the selected integrity verification before actors are registered. A
previously completed input is returned without another provider call; an admitted nonterminal input is failed closed
rather than replayed.

## Package and engine delivery

All `@opengameagent/*` workspaces are MIT-licensed source packages. Their `private: true` flag prevents accidental npm
publication; it does not restrict use under the license. This repository does not publish the TypeScript packages to a
registry. Consume a pinned source checkout and build the workspaces, or package reviewed builds for your distribution.
`npm run package:check` validates package metadata and included license notices, then dry-packs every workspace.
Keep the OpenGameAgent copyright and license notice with distributions, together with the notices required by the
dependencies you ship; see [third-party notices](../THIRD_PARTY_NOTICES.md).

The complete engine surfaces are:

- Unity 6 package: `engines/unity/Packages/com.opengameagent.client`
- Godot C# integration: `engines/godot`
- Unreal 5.8 plugin: `engines/unreal/OpenGameAgent`

C# and C++ under those directories are engine implementation substrates, not separate general-purpose SDK products.
See [verification boundaries](verification.md) for the evidence provided by the portable, Linux, and real-editor gates.
