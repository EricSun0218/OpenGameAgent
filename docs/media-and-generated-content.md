# Media and generated content

`GameAgent.Generation` connects an in-engine Agent to image, video, speech, and
structured-content APIs without bundling a model. A provider can point at a
remote service or an explicitly enabled local HTTP endpoint.

## Configure a provider and stores

```csharp
var provider = new MediaHttpGenerationProvider(
    new MediaHttpProviderOptions
    {
        Name = "content-api",
        BaseUri = new Uri("https://generation.example.com/"),
        ImagePath = "/v1/images/generations",
        VideoPath = "/v1/videos",
        SpeechPath = "/v1/audio/speech"
    },
    credentialSource);

var jobs = new FileGenerationJobStore(jobStateDirectory);

var artifacts = new FileGenerationArtifactStore(
    new FileGenerationArtifactStoreOptions
    {
        RootDirectory = artifactDirectory,
        AllowedRemoteHosts = new[] { "generation.example.com" }
    },
    artifactAuthorizationProvider);

var generation = new GenerationRuntime(
    new[] { provider },
    jobs,
    artifacts);
```

`AllowedRemoteHosts` applies to artifact downloads, including provider-returned
URLs. Redirects are rejected. Loopback URLs are rejected by default; set
`AllowLoopbackHttp` only for a deliberately configured local provider. When a
custom `HttpClient` is injected, its handler and redirect policy are part of the
application's trusted transport boundary.

## Submit and observe work

```csharp
var job = await generation.SubmitAsync(new GenerationRequest
{
    OperationId = "portrait:npc-42:v3",
    IdempotencyKey = "portrait:npc-42:v3",
    Modality = GenerationModalities.Image,
    Model = "configured-image-model",
    Input = ProtocolJson.ParseElement(
        """{"prompt":"painted character portrait"}""")
});

if (!GenerationJobStatuses.IsTerminal(job.Status))
{
    job = await generation.WaitForCompletionAsync(job.OperationId);
}
```

Reusing an operation ID with identical input returns its durable job. Reusing it
with different input is rejected. A cancellation or transport interruption
after dispatch can have an unknown provider outcome; the runtime records that
state and requires polling or reconciliation instead of pretending the request
was never accepted.

Immediately before provider dispatch, the runtime persists an uncertain
dispatch checkpoint. A process loss can therefore never turn an ambiguous
provider submission into an automatic duplicate request. A request that was
durably queued but never dispatched may be resumed by submitting the exact same
request and operation ID.

Provider artifact sources are checkpointed before materialization, including
the source required to recover a synchronous response that has no provider job
ID. Artifacts are imported into game-controlled local storage before a job
becomes locally `succeeded`. The file store enforces an allowlisted host,
maximum bytes, optional declared size and SHA-256, known media signatures, and
content-addressed filenames.

The common HTTP adapter accepts at most 32 MiB per inline artifact by default;
larger outputs should use a provider URL. The durable file job store allows a
64 MiB record by default so the base64-encoded recovery checkpoint remains
bounded. Inline image bytes are removed from the provider metadata copy rather
than being persisted twice. Lower both limits for games that never need large
inline responses.

## Expose generation to an Agent

`GenerationToolBridge.Tools` returns descriptors for:

- `generate_image`;
- `generate_video`;
- `generate_speech`;
- `generate_structured_content`;
- `get_generation`;
- `cancel_generation`.

Register the descriptors in the normal tool catalog and delegate matching host
actions to `GenerationToolBridge.TryHandleAsync`. They still pass through the
ordinary write-ahead action and receipt boundary. An accepted asynchronous job
returns a successful tool receipt whose structured result contains its actual
job status; success does not imply that a queued video is already complete.

## Streaming speech

`StreamingSpeechRuntime` provides bounded first-capable routing for providers
that implement `IStreamingSpeechProvider`. Start events are buffered until the
provider produces audio or a clean completion, so a rejected provider can be
replaced without exposing two stream lifecycles. Sequence numbers must advance,
the media type must remain stable, and every successful stream must emit a
completion event. Once bytes have been emitted, a provider failure ends with an
uncertain interruption rather than splicing audio from another voice or model.

## Admit generated content into the game

Model or media output is not automatically trusted game content.
`GeneratedContentCoordinator` uses a recoverable host-owned transaction:

1. persist `Prepared`;
2. mark `Staging`, then let the game stage assets and data;
3. mark `Validating`, then let the game check schema, references, budgets,
   permissions, and scripts;
4. mark `Committing`, then require a durable game receipt;
5. reconcile any uncertain stage, abort, or commit through
   `IGeneratedContentHost.GetStatusAsync`.

The manifest can carry structured data, imported artifacts, dependencies,
provenance, and inert script source. The game decides whether scripts are
forbidden, interpreted in a sandbox, compiled out of process, or otherwise
admitted. The runtime never executes generated source.

## Godot and Unity

Godot exposes dictionary-based submit, refresh, wait, and cancellation methods,
plus `GenerationUpdated` and `GenerationFailed` signals. The Variant bridge
normalizes and bounds the request before it reaches the shared runtime.

Unity exposes typed asynchronous methods and `GenerationUpdated` /
`GenerationFaulted` events on `UnityAgentRuntimeHost`. Terminal observation is
posted through the engine's bounded event path and generation lifetime is
cancelled during host shutdown.
