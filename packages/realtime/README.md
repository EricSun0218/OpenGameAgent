# @opengameagent/realtime

Provider-neutral realtime voice coordination for a game-owned dialogue channel. Final recognized text is projected into
the existing `GameRuntime`, so Pi remains the only production message/Tool Agent loop. The package does not own a
dialogue graph, narrative variables, localization, microphone permissions, device routing, VAD policy, provider
credentials, game writes or a second conversation history.

`GameRealtimeVoiceService` fences each utterance by session, actor, voice session, positive epoch and utterance ID.
The explicit interrupt operation requires that exact coordinate. Partial recognition text is ephemeral; exactly one final transcript
becomes the runtime input. After that final transcript, the host projector may asynchronously read its current
authoritative game state before the service starts exactly one runtime run. It receives the turn's abort signal, and an
exact interrupt or caller cancellation ends the wait so a late projection cannot start a stale run. Only canonical
owner-visible assistant text is sent to TTS. Hidden reasoning, Tool details and internal events are never spoken. Empty
and Tool-only responses do not start TTS.

The input projector may return `admissionAuthority: { scope, generation }` alongside `inputId` and `payload`.
The service captures it with shared protocol validation before forwarding it to `GameRuntime.start` for admission.
Closing that scope and generation interrupts its active admitted voice run; a later generation can start on the same actor.
When a runtime run aborts or fails, the service also cancels pending synthesis and discards late audio so the voice
channel can be reused. Successful runs finish their pending speech normally.

An exact interrupt marks the old utterance before it waits for runtime cancellation. A different newer coordinate may
therefore take over the same voice channel even if a non-cooperative old provider has not settled; the old turn's
identity-checked cleanup cannot remove or interrupt the successor. An active, uninterrupted turn keeps the channel
busy. Runtime cancellation uses the owned session/actor/run ID atomically, so Pi Tool turn
advances cannot redirect the abort to a newer run. The caller's in-process abort reason reaches provider, Tool and
context signals but is not persisted.

Recognizers and synthesizers are narrow host-supplied ports. Input, transcript, assistant text, output audio and the
single-consumer event/text handoffs are bounded by mandatory host limits with no defaults. Backlog overflow aborts the
turn instead of accumulating unbounded work. Audio remains ephemeral in this package; durable generated audio belongs
to the existing media job/resource/Action path.

The service can optionally expose two passive `realtime` health dependencies, one for recognition and one for
synthesis. Both start `declared`. A valid final transcript from a real utterance makes only recognition `ready`; a
real synthesis becomes `ready` only after it has consumed the complete closed visible-text input and completed with at
least one audio chunk. An empty final transcript, early synthesis completion, typed capacity failure, or dependency
failure makes the affected side `unavailable` without rewriting the other side. Expired evidence becomes `available`
with reason `stale`. Reading `healthDependencies()` never sends empty audio, synthesizes empty text, opens a provider
session, or otherwise probes a dependency. The host must configure separate stable non-secret IDs, requirements and
positive safe-integer evidence lifetimes, plus the shared clock. The coordinator still owns no provider URL,
credential, timeout or polling policy.

Every `RealtimeVoiceLimits` field is required:

- `maxIdentifierBytes` bounds session, actor, voice-session, epoch, utterance, voice-profile and projected input IDs.
- `maxMediaTypeBytes` bounds each captured input/output audio media type.
- `maxInputChunks`, `maxInputChunkBytes` and `maxInputAudioBytes` bound one recognized utterance.
- `maxRecognitionEvents`, `maxRecognitionTextBytes` and `maxTranscriptBytes` bound ephemeral hypotheses and the final
  transcript before a runtime input is created.
- `maxAssistantTextEvents`, `maxAssistantTextBytes` and `maxPendingTextSegments` bound canonical visible-text projection
  and TTS backpressure.
- `maxOutputChunks`, `maxOutputChunkBytes` and `maxOutputAudioBytes` bound one synthesized response.
- `maxEventBacklog` bounds the single-consumer public event stream.

The repository includes a host-run backpressure acceptance workload:

```sh
npm run build
npm run benchmark:realtime -- --config scripts/benchmark-profiles/realtime-backpressure-8x32.json
```

It exercises stalled public-event and stalled TTS-text consumers through the real runtime, then runs a newer turn on the
same voice channel. It requires exact fail-closed boundaries, provider cleanup, late-audio rejection and clean channel
reuse. Profile capacities and measured timings are benchmark evidence only; they are not runtime defaults or SLAs.

All are positive safe integers supplied by the host; negative zero is rejected. The positive decimal epoch is a fencing
identity rather than a timeout. Sample rate and channel count, when present, are positive safe integers owned by the
selected audio Adapter. The coordinator defines no timeout, retry, silence duration, endpointing/VAD threshold, session
duration, packet cadence, provider fallback count or codec default.

Completion is fail-closed: if visible text, audio or the terminal public event cannot enter its bounded handoff, the
completion result is failed even when Pi itself completed. Iterator methods are captured once, source audio is closed
once after final recognition, and late provider chunks lose the abort race and are not emitted.

`@opengameagent/realtime-local` supplies optional finite or continuous VAD endpointing, an optional hash-pinned local
Silero ONNX detector, and concrete OpenAI-compatible STT/TTS adapters while preserving this runtime and authority
boundary. Other provider or transport adapters remain separate packages and must not introduce another Agent/Tool loop.
