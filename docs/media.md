# Generated media

OpenGameAgent defines provider-neutral image, audio, and video generation contracts. It does not include a model, asset database, editor, moderation policy, or runtime decoder.

## Contracts

- `GameMediaGenerationRequest` carries a stable request ID, media kind, structured context, provider parameters, optional prompt, and source resource references.
- `IGameMediaGenerator` performs generation and reports bounded progress.
- `GameMediaGenerationResult` returns one or more `ResourceContent` references plus structured metadata.
- `GameMediaGenerationProgress` may carry a bounded preview resource. When generation is exposed as a tool, inline data is converted to typed image/audio/video progress content for the host UI.
- `GameMediaGenerationTool` exposes a generator to the agent as a non-idempotent write by default; a stable request ID lets the media service deduplicate or resume submissions when it implements that guarantee.

`OpenGameAgent.Media` adds a provider/model registry on top of these contracts. It validates model capability, media kind, authentication, request/result limits, refresh races, cancellation, and timeouts, then returns an in-band completed/failed/canceled generation result. The registry retains the underlying generator's progress and async job behavior.

`OpenGameAgent.Providers.MediaHttp` implements a bounded JSON HTTP transport for cloud or local APIs that implement the documented request/job shape. If a service uses different fields, authentication, upload semantics, or durable job handles, adapt it behind `IGameMediaGenerator` instead of pretending the wire formats are interchangeable. The game is responsible for downloading or importing resources after validating origin, content type, size, checksum, license metadata, storage quota, and content policy.

`OpenGameAgent.Providers.OpenRouter` is a dedicated image-generation adapter with model discovery, text and image references, buffered or SSE results, progressive previews, usage metadata, and the same unified provider authentication used by the media registry. It is separate from the generic HTTP adapter because its wire contract is different.

`OpenGameAgent.Providers.OpenAI.Images` calls the official Images generation/edit endpoints. It sends one or more resolved `GameMediaGenerationRequest.Sources` as multipart `image[]` fields for edits and returns validated inline PNG, JPEG, or WebP resources. `OpenGameAgent.Providers.Volcengine.Images` calls the Ark/Seedream generations endpoint, sends references as a bounded data-URL array, defaults to `stream=false` and `watermark=false`, and supports explicit sizes such as `2048x1152`. Both adapters use `IGameProviderAuthentication`; prompts, credentials, and reference bytes are excluded from returned metadata and error messages.

Both direct adapters accept only inline PNG, JPEG, or WebP sources whose MIME type matches their bytes. Resolve attachment IDs or game-owned files before invoking the provider. Their default HTTP transport disables redirects, remote endpoints require HTTPS, and explicitly enabled plaintext HTTP is limited to loopback development endpoints. OpenAI sizes are strictly limited to `auto`, `1024x1024`, `1024x1536`, and `1536x1024`; `2048x1152` is rejected rather than fabricated. Seedream accepts an explicit `2048x1152` request. A game that needs an OpenAI result in that canvas should perform a deterministic post-generation letterbox or other game-owned transform.

Use `GetApiKeyAsync` when credentials rotate or expire during long-running jobs. Status URLs are restricted to the submission endpoint's origin by default. If cross-origin polling is enabled, authorization is still withheld from the other origin unless `SendAuthorizationToCrossOriginStatusUrls` is explicitly enabled.

## Recommended flow

```text
agent requests media
  -> game creates stable request ID
  -> provider returns progress and resource references
  -> game downloads into quarantine
  -> validate bytes and policy
  -> import through engine asset pipeline
  -> commit game-owned asset record
```

Do not treat a URL returned by a model or generation endpoint as a safe game asset. Avoid loading remote bytes directly into a renderer, audio decoder, or video decoder without bounds and validation.

The generic HTTP adapter keeps polling while one call is alive and sends the stable request ID on submission. A media service should make that ID idempotent so retrying the same game request resumes or returns the same job instead of charging twice. The generic contract does not expose a portable mid-job handle. If a product must survive a process restart while a long video or batch job is pending, use a service-specific `IGameMediaGenerator` or workflow step that persists the provider job ID in game-owned state.

## HTTP shape

The adapter submits camel-case JSON with `requestId`, `kind`, `prompt`, `context`, `parameters`, and `sources`. A successful endpoint returns either an immediately completed job or a queued/running job. Job documents use `status`, optional `statusUrl`, optional `retryAfterMs`, optional `progress`, and completed `outputs` containing `uri`, `mediaType`, and optional `name`. Optional `metadata`, `requestId`, and `error` fields are preserved. Response bodies, source/output counts, polling attempts, retry intervals, and request bytes are bounded by `HttpMediaGeneratorOptions`.
