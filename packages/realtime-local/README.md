# @opengameagent/realtime-local

Optional local speech endpointing and OpenAI-compatible STT/TTS adapters for game developers. This package composes
with `@opengameagent/realtime`; it does not own dialogue state, game rules, device permissions, a conversation history,
or an Agent/Tool loop. Final recognized text still enters the existing `GameRuntime`, where Pi remains the only
production Agent loop.

## Endpointing and barge-in

`VoiceActivityUtteranceCapture.capture()` turns one finite stream of signed little-endian interleaved PCM16 chunks into
one replayable utterance. `openSession()` instead owns one longer source and detector session and yields consecutive
utterances through `captureNext()`, preserving detector state and the unconsumed source tail between utterances. The
public format is the canonical `audio/pcm;format=s16le`; other media types fail closed instead of being silently
reinterpreted. Detector-session and source-iterator cleanup is requested exactly once; closing an owned session also
settles a pending source read without waiting for a non-cooperative iterator.

The host explicitly supplies activation/deactivation probabilities, consecutive speech and silence frame counts,
prefix padding, and all frame/byte limits. There are no threshold, cadence, silence-duration, or capacity defaults.
Detector frames use the cadence chosen by the host adapter; the package does not reinterpret frame counts as time.
Source and detector cleanup is requested exactly once and then detached, so a non-cooperative host cleanup cannot hide
the primary result. A capturer must preserve the declared PCM format; explicit resampling belongs before this pipeline.

`GameLocalSpeechPipeline` starts an exact-coordinate interruption when confirmed speech begins. That interruption is
settled independently so a slow prior provider cannot stop microphone capture or endpointing. A new runtime turn starts
only after the interruption result is known. An already-ended prior turn is accepted, while a coordinate mismatch
fails closed. A finite candidate with no confirmed speech causes neither an interruption nor a runtime turn.

`GameContinuousLocalSpeechSession` composes that endpointing session with the same `GameLocalSpeechPipeline` and
`GameRealtimeVoiceService`. The host supplies one exact coordinate for each detected utterance. Confirmed speech for the
next utterance interrupts only the preceding exact coordinate before the next Runtime turn starts. Started-turn handoff
is explicitly bounded, and stop, cancellation, source end, detector failure, or an abandoned turn iterator closes the
owned endpointing session and exact active voice turn. The host still owns microphone permission, device selection,
capture buffering, resampling, playback, and the coordinate source.

## Optional Silero detector

`SileroVoiceActivityDetector` is a thin local `onnxruntime-web` Adapter for the official Silero VAD ONNX contract. It
accepts only mono PCM16 at 16 kHz and exactly 512 samples (1024 bytes) per detection call. Each opened stream owns fresh
64-sample context and `[2, 1, 128]` recurrent state; successful inference alone advances them. Model outputs, timeouts,
caller cancellation, concurrent stream capacity, and disposal fail closed without exposing backend error text.

The game must supply a local model path, its exact lowercase SHA-256, model-path and model-byte limits, model-load and
inference deadlines, and maximum concurrent detector sessions. The Adapter verifies the bounded file before ONNX
loading and validates the model input/output contract. It does not bundle or download a model. The host is responsible
for choosing a compatible model artifact and complying with its license.

## OpenAI-compatible speech

`OpenAICompatibleSpeechRecognizer` uses the official OpenAI Node client and the multipart transcription endpoint. It
accepts only the canonical `audio/pcm;format=s16le` input media type, validates PCM16 alignment, bounds chunks and bytes, and creates a
deterministic WAV envelope from the request's sample rate and channel count. It emits exactly one bounded final
transcript; speculative partial text is not fabricated.

The same provider-neutral envelope operation is exported as `collectPcmS16LeWav()`. A caller supplies the exact PCM16
format, owned audio stream, cancellation signal, and chunk/per-chunk/aggregate limits. It snapshots aligned chunks,
closes the source exactly once, and returns one deterministic RIFF/WAVE byte array. It does not write a file, select a
speech provider, or retain audio.

`OpenAICompatibleSpeechSynthesizer` consumes the complete bounded visible-text stream before one speech request, then
streams bounded binary chunks. The response `Content-Type` must match the configured game output media type. The host
must configure an output format that describes the selected provider/model and an engine decoder that accepts the
chosen OpenAI response format. The configured output uses one bare media type; response header case and parameters are
normalized before comparison.

Both adapters require the endpoint, credential, model, request deadline, and every identifier/media/chunk/byte limit.
They make one non-retried POST per non-empty operation, reject redirects, apply one authoritative host deadline through
response streaming, and sanitize provider failures. Plain HTTP is admitted only for loopback endpoints; other endpoints require
HTTPS. Caller cancellation preserves the exact in-process abort reason. Audio is ephemeral and is never written to
disk by this package.

The package does not select or download speech models, open microphones or speakers, resample audio, decode compressed
formats, add a network listener, or probe providers. Those remain explicit game-host integration decisions.
