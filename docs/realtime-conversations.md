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

Both target `netstandard2.1`. They can run in a Godot/Unity process, a local sidecar, or a .NET service. A shipped client still must not contain a permanent developer API key.

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

## Interruption semantics

When input speech begins during output, the manager cancels the active response and truncates the provider conversation item at the duration of audio emitted so far. Speech that the player did not hear is therefore not retained as if it had been heard.

Audio capture uses a bounded drop-on-full queue. Text, handoff output, and control commands use bounded backpressure. Incoming provider events are drained independently from background agent work, so a handoff cannot stop microphone forwarding.

By default, provider `handoff` requests are consumed by `GameRealtimeAgentBridge`. Set `ClientManagedHandoffs = true` when the host needs to inspect or route every request itself; the requests remain observable but the automatic bridge will not claim them. With `FlushTranscriptTailOnClose` enabled, accepted input transcript that was not handed off before shutdown is emitted once as an `IsTranscriptTail` handoff. The automatic bridge commits that tail to the agent without attempting to speak back through the already closing realtime session.

## Behavior versus authoritative action

Realtime `behavior` requests are replaceable by channel. Typical channels are `gaze`, `gesture`, `expression`, and `locomotion`. The handler receives a cancellation token when a newer behavior supersedes an older one or the session closes.

Do not use this path for a committed mutation. A build operation, inventory transfer, attack result, resource collection, or quest update is a normal game tool. It must pass the game's validation and, when retry safety matters, the durable action journal and receipt boundary.

## Placement and transport

`IRealtimeTransport` is provider-neutral. The included OpenAI adapter uses a credential-free `wss` endpoint plus an authorization handshake header, validates and bounds JSON/audio, and rejects remote plaintext WebSockets. A game can implement the same contract for a local model, WebRTC session, platform voice SDK, or developer-hosted gateway without changing the conversation manager or bridge.

The included adapter is WebSocket-based. Browser or platform WebRTC negotiation remains transport-specific and should be implemented behind `IRealtimeTransport`; it is not emulated by the runtime.
