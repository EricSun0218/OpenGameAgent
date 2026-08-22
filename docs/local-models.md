# Local models and media

`OpenGameAgent.Providers.Local` is the optional composition package for running OpenGameAgent against services on the developer's or player's machine. It does not bundle or download models. The game chooses which local service to trust and when to start, stop, warm, or update it.

The package keeps local execution behind the same contracts used by hosted providers:

- text, structured context, image input, reasoning events, and tool calls use `IGameModelProvider`;
- realtime PCM16 speech uses `IRealtimeTransport`;
- generated images, audio, and video use `IGameMediaGenerator` and `GameMediaModelRegistry`;
- durable world mutations still pass through game-owned tools and authoritative receipts.

## Text, vision, reasoning, and tools

The built-in endpoint profiles cover Ollama, LM Studio, LocalAI, llama.cpp, and vLLM. Discovery is explicit and bounded. LocalAI capability discovery is used when available; other OpenAI-compatible services default to text-only until the host supplies capability overrides. Unknown models are never silently advertised as tool- or vision-capable, and local cost remains explicitly unknown.

```csharp
using OpenGameAgent.Models;
using OpenGameAgent.Providers.Local;

var http = new HttpClient();
var options = LocalGameModelPresets.Ollama(http);
options.OutputCapabilityOverrides = new Dictionary<string, GameModelOutputCapabilities>
{
    ["qwen2.5:latest"] = GameModelOutputCapabilities.Text
        | GameModelOutputCapabilities.ToolCalls,
};

var local = new LocalGameModelEndpoint(options);
var health = await local.ProbeAsync(cancellationToken);
if (health.Health != LocalGameEndpointHealth.Available)
{
    throw new InvalidOperationException(health.ErrorCategory);
}

var catalog = new GameModelCatalog();
catalog.Register(local.CreateRegistration());
await catalog.RefreshAsync("ollama", allowNetwork: true, force: true, cancellationToken);
var provider = catalog.CreateProvider("ollama");
```

`ProbeAsync` returns bounded, non-sensitive health metadata: state, elapsed time, category, HTTP status, and discovered descriptors. It never returns response bodies or credentials.

The presets use loopback endpoints and no credentials by default. Remote endpoints require an explicit opt-in; remote HTTP requires a second explicit opt-in. A game that exposes a local service beyond loopback owns authentication and transport security.

## Local embeddings

`LocalOpenAIEmbeddingProvider` connects the existing vector-memory lifecycle to an OpenAI-compatible `/v1/embeddings` endpoint. The host must supply a stable provider ID, model ID, weights/version ID, and vector dimensions. Changing any of them changes `MemoryEmbeddingIdentity`, allowing `VectorMemoryStore` to report that its rebuildable index no longer matches the active model.

```csharp
var embeddings = new LocalOpenAIEmbeddingProvider(
    new LocalOpenAIEmbeddingProviderOptions(
        new HttpClient(),
        new Uri("http://127.0.0.1:8080/v1"),
        providerId: "localai",
        modelId: "bge-m3",
        modelVersion: "weights-v1",
        dimensions: 1024)
    {
        QueryPrefix = "query: ",
        DocumentPrefix = "passage: ",
    });
```

The adapter preserves batch ordering, validates exact dimensions and finite values, bounds text count and size plus request/response bytes, and shares the provider authentication boundary. It does not own the authoritative memory store or index files.

## Local realtime speech

LocalAI and Speaches expose OpenAI-compatible realtime WebSocket endpoints. Their presets reuse `OpenAIRealtimeTransport` and explicitly permit anonymous access only on loopback:

```csharp
using OpenGameAgent.Providers.Local;
using OpenGameAgent.Providers.OpenAI.Realtime;

var transport = new OpenAIRealtimeTransport(LocalRealtimePresets.Speaches());
```

The same `RealtimeConversationManager` supplies PCM16 input/output, transcription events, subtitle timing, barge-in, truncation, steering, and handoff. Local speech does not create a separate authority path: durable actions remain in `GameAgentRuntime`.

Services that expose separate VAD/STT/TTS APIs can use `ComposableRealtimeTransport`. It accepts a session-scoped `IGameVoiceActivityDetector`, a concurrent `IGameSpeechRecognizer`, and a streaming `IGameSpeechSynthesizer`, then projects them through the same realtime events and `GameRealtimeAgentBridge`. `EnergyGameVoiceActivityDetector` is a deterministic bounded default; an engine or ONNX VAD can replace it without changing the bridge.

The local package includes OpenAI-compatible `/v1/audio/transcriptions` and `/v1/audio/speech` adapters. They support PCM16 WAV transcription input and validated raw PCM16 or WAV speech output:

```csharp
var speech = new ComposableRealtimeTransport(
    new LocalOpenAISpeechRecognizer(new(
        http,
        new Uri("http://127.0.0.1:8000/v1"))
    {
        Model = "whisper-1",
    }),
    new LocalOpenAISpeechSynthesizer(new(
        http,
        new Uri("http://127.0.0.1:8000/v1"))
    {
        Model = "kokoro",
        OutputFormat = LocalOpenAISpeechOutputFormat.Pcm16,
        RawPcmSampleRate = 24_000,
    }));

await using var conversation = new RealtimeConversationManager(speech);
// Attach the normal GameRealtimeAgentBridge, then start and feed PCM16 frames.
```

Utterance, queue, transcript, request, response, frame, timeout, and pre-roll limits are explicit. Speech onset cancels active and queued synthesis even before its first audio frame, while authoritative actions already dispatched through the game journal are unaffected.

## Explicit local model lifecycle

`LocalGameModelLifecycle` is a developer/host control surface for inventory, warmup, load, unload, and acquisition. Nothing invokes it automatically from a prompt or agent run. Operations have bounded concurrency and timeout; acquisition is rejected before the backend unless `AuthorizeAcquisitionAsync` explicitly grants that request.

```csharp
using var lifecycle = new LocalGameModelLifecycle(
    new OllamaGameModelLifecycleBackend(
        new OllamaGameModelLifecycleOptions(http)),
    new LocalGameModelLifecycleOptions
    {
        AuthorizeAcquisitionAsync = (request, cancellationToken) =>
            new ValueTask<bool>(developerSettings.AllowModelDownloads),
    });

var installed = await lifecycle.ReadInventoryAsync(refresh: true, cancellationToken);
await lifecycle.WarmupAsync("qwen2.5:7b", cancellationToken);
await lifecycle.AcquireAsync(
    new LocalGameModelAcquisitionRequest("qwen2.5:7b"),
    (progress, cancellationToken) =>
    {
        ShowProgress(progress.Stage, progress.Ratio);
        return default;
    },
    cancellationToken);
```

The Ollama backend merges installed and running inventory, uses explicit keep-alive load/unload calls, and parses bounded pull progress. Other local runtimes implement `ILocalGameModelLifecycleBackend`; model files, license acceptance, disk placement, and acquisition policy remain host-owned.

## LocalAI image, video, and speech generation

Register the exact local models configured by the host:

```csharp
using OpenGameAgent.Media;
using OpenGameAgent.Models;
using OpenGameAgent.Providers.Local;

using var media = new GameMediaModelRegistry();
media.Register(LocalAiMediaProvider.CreateRegistration(
    new LocalAiMediaProviderOptions(),
    new StaticGameProviderAuthentication(),
    new[]
    {
        LocalAiMediaProvider.CreateImageModel("local-image"),
        LocalAiMediaProvider.CreateVideoModel("local-video"),
        LocalAiMediaProvider.CreateSpeechModel("local-voice"),
    }));
```

The adapter uses LocalAI's image generation, video, and speech endpoints. Outputs are decoded, size-bounded, signature-checked, and returned as ordinary `ResourceContent` for the existing generated-asset pipeline. The package does not guess installation-specific image-edit semantics; use the workflow adapter when source images must enter a graph.

## ComfyUI workflows

ComfyUI graphs are supplied by trusted game code through `ComfyUiWorkflowFactory`. Model output cannot submit an arbitrary graph unless the host deliberately implements that policy. Source images are uploaded first, and the factory receives only their validated ComfyUI references.

```csharp
var registration = ComfyUiMediaProvider.CreateRegistration(
    new ComfyUiMediaProviderOptions((context, cancellationToken) =>
    {
        var sourceName = context.Sources.Count == 0 ? null : context.Sources[0].FileName;
        return ValueTask.FromResult(new ComfyUiWorkflowDefinition(
            BuildTrustedWorkflow(context.Request.Prompt, sourceName)));
    }),
    new StaticGameProviderAuthentication(),
    new[]
    {
        ComfyUiMediaProvider.CreateModel(
            "portrait-workflow",
            GameModelOutputCapabilities.Image),
    });
```

The adapter bounds source count and bytes, graph size and depth, polling, response size, output count, and output bytes. Redirects and cross-origin credential forwarding are rejected. Cancellation attempts to interrupt the submitted job.

## Host responsibilities

- Install and configure the local service and models, or provide a separate installer UX.
- Declare exact model capabilities when discovery cannot prove them.
- Keep remote access disabled unless transport security and authentication are configured.
- Run model and media work away from the engine main thread.
- Treat model output as untrusted; validate every game mutation in the authoritative game layer.
- Set hardware-aware context, concurrency, queue, timeout, and media limits. OpenGameAgent bounds requests but cannot infer available VRAM.

Local execution changes privacy, latency, cost, hardware, and distribution trade-offs. It does not weaken OpenGameAgent's tool policy, approval, journal, conflict, receipt, or save-generation boundaries.
