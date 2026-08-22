# Realtime conversations

Realtime conversation is an optional duplex layer around `GameAgentRuntime`. It is designed for a character that keeps listening and speaking while a slower planning/tool loop works in the background.

```text
microphone PCM16 -> bounded audio queue -> realtime transport -> audio/transcripts
                                              |
                                              | handoff
                                              v
                                  GameRealtimeAgentBridge
                                              |
                                  start or steer actor run
                                              |
                         model stream -> tools -> authoritative receipts
                                              |
                                bounded text updates (~200 ms)
                                              v
                                      realtime transport
```

This is intentionally two loops. The realtime model owns turn-taking, transcription, speech, and reversible presentation cues. The game agent owns planning and tools. Game code remains authoritative over world state.

## Packages

- `OpenGameAgent.Realtime`: provider-neutral contracts, bounded conversation manager, behavior channel orchestration, and `GameAgentRuntime` handoff bridge.
- `OpenGameAgent.Providers.OpenAI.Realtime`: OpenAI Realtime WebSocket wire adapter. Credentials are used only during the WebSocket handshake.
- `OpenGameAgent.Providers.Volcengine.Realtime`: Volcengine duplex speech adapter. Dialogue audio is used for VAD/transcription and handed to the OGA agent; agent text is streamed through a separate bidirectional TTS session. The provider's own chat output never becomes a second game-action loop.
- `OpenGameAgent.Providers.Local`: OpenAI-compatible local STT/TTS adapters which compose through `ComposableRealtimeTransport`; models and services are supplied by the host.

Both target `netstandard2.1`. They can run in a Godot/Unity process, a local sidecar, or a .NET service. A shipped client still must not contain a permanent developer API key.

For separate speech components, `ComposableRealtimeTransport` turns bounded VAD decisions and complete PCM16 utterances into input transcript/handoff events, then streams Agent handoff text through TTS. It uses `GameRealtimeAgentBridge`, so tool calls, steering, receipts, conflicts, and durable actions remain on the existing runtime. Speech cancellation only discards presentation output; it does not roll back or replay a committed world action.

## Minimal setup

```csharp
using OpenGameAgent;
using OpenGameAgent.Providers.OpenAI.Realtime;
using OpenGameAgent.Realtime;

var transport = new OpenAIRealtimeTransport(new OpenAIRealtimeTransportOptions
{
    ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
});

await using var conversation = new RealtimeConversationManager(
    transport,
    behaviorHandler: myEngineThreadBehaviorHandler);

await using var bridge = new GameRealtimeAgentBridge(
    gameRuntime,
    conversation,
    new GameSessionKey("save-1", "npc-17"),
    (handoff, cancellationToken) => new ValueTask<GameInput>(new GameInput(
        "save-1",
        "npc-17",
        "realtime_handoff",
        JsonSerializer.Serialize(new { transcript = handoff.Transcript }),
        currentGameMoment(),
        inputId: handoff.HandoffId)));

using var events = conversation.RegisterHandler((value, cancellationToken) =>
{
    // Queue audio/transcript UI delivery to the engine main thread here.
    return default;
});

await conversation.StartAsync(new RealtimeConversationOptions
{
    Model = "gpt-realtime-1.5",
    Voice = "alloy",
    Instructions = "Stay in character. Delegate planning and world actions."
});

// Call from the audio capture callback. False means the bounded queue was full.
conversation.TrySendAudio(new RealtimeAudioFrame(pcm16Bytes, 24_000, 1));
```

For Volcengine, replace only the transport. The API key is sent in the WebSocket handshake and is never copied into events, exceptions, transcripts, or saved state:

```csharp
using OpenGameAgent.Providers.Volcengine.Realtime;

var transport = new VolcengineRealtimeTransport(new VolcengineRealtimeTransportOptions
{
    ApiKey = Environment.GetEnvironmentVariable("VOLCENGINE_TTS_API_KEY"),
    DialogueResourceId = "volc.speech.dialog",
    TtsResourceId = "seed-tts-2.0",
    TtsModel = "seed-tts-2.0-standard",
    Speaker = "your-registered-voice-id",
});
```

The Volcengine adapter accepts mono PCM16 at 16 kHz and emits mono PCM16 at the configured output rate (24 kHz by default). It maps provider speech boundaries and input transcripts into standard handoffs, exposes word-level subtitle timing when returned, and streams OGA handoff text into bounded TTS sub-sessions. Set `InputMode = VolcengineRealtimeInputMode.Disabled` for TTS-only use.

Set `RealtimeConversationOptions.Voice` to a registered Volcengine speaker ID when an NPC needs a per-session voice. The framework placeholder `alloy` (or an empty value when calling the transport directly) falls back to `VolcengineRealtimeTransportOptions.Speaker`. The selected speaker is snapshotted per connection, so concurrent NPC sessions cannot overwrite each other's voice.

Transports may implement `IRealtimeTransportCapabilities`. Hosts can use its flags to select UI and fallbacks without provider-name checks. Capability flags describe executable behavior, not merely fields accepted by an options object.

## Interruption semantics

When input speech begins during output, the manager cancels the active response. A transport with remote conversation items can also truncate the item at the duration of audio emitted so far. The Volcengine adapter has no provider-owned OGA conversation item to truncate: it cancels the TTS sub-session and drops late audio while the authoritative agent transcript remains in OGA.

Audio capture uses a bounded drop-on-full queue. Text, handoff output, and control commands use bounded backpressure. Incoming provider events are drained independently from background agent work, so a handoff cannot stop microphone forwarding.

By default, provider `handoff` requests are consumed by `GameRealtimeAgentBridge`. Set `ClientManagedHandoffs = true` when the host needs to inspect or route every request itself; the requests remain observable but the automatic bridge will not claim them. With `FlushTranscriptTailOnClose` enabled, accepted input transcript that was not handed off before shutdown is emitted once as an `IsTranscriptTail` handoff. The automatic bridge commits that tail to the agent without attempting to speak back through the already closing realtime session.

## Behavior versus authoritative action

Realtime `behavior` requests are replaceable by channel. Typical channels are `gaze`, `gesture`, `expression`, and `locomotion`. The handler receives a cancellation token when a newer behavior supersedes an older one or the session closes.

Do not use this path for a committed mutation. A build operation, inventory transfer, attack result, resource collection, or quest update is a normal game tool. It must pass the game's validation and, when retry safety matters, the durable action journal and receipt boundary.

## Placement and transport

`IRealtimeTransport` is provider-neutral. The included OpenAI and Volcengine adapters use authorization handshake headers, validate and bound JSON/audio, redact configured secrets, and reject remote plaintext WebSockets. A game can implement the same contract for a local model, WebRTC session, platform voice SDK, or developer-hosted gateway without changing the conversation manager or bridge.

The included adapter is WebSocket-based. Browser or platform WebRTC negotiation remains transport-specific and should be implemented behind `IRealtimeTransport`; it is not emulated by the runtime.
