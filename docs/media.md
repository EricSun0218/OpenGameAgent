# Generated media

OpenGameAgent defines one provider-neutral contract for image, audio, and video generation. It does not bundle model weights, an asset editor, a moderation service, or a game decoder.

## Core contracts

- `GameMediaGenerationRequest` carries a stable request ID, exact game session, media kind, prompt, optional parameters, and zero or more bounded binary sources.
- `GameMediaGenerator` performs generation and may emit bounded progress.
- `GameMediaGenerationResult` returns validated binary outputs plus provider/model identity, response ID, and usage when available.
- `GameMediaRegistry` selects a registered provider/model, validates request and result limits, applies cancellation and a hard timeout, and rejects provider identity or media-kind mismatches.
- `DurableGameMediaPipeline` adds crash-safe generation, content-addressed resources, and authoritative engine import. See [Generated assets and authoritative import](generated-assets.md).
- `createDurableGameMediaTool` exposes that pipeline as an ordinary Agent Tool while keeping canonical recovery coordinates out of the default model-visible projection.

## Included adapters

| Adapter | Use |
| --- | --- |
| Catalog-backed image adapter | Shared image-model registry, including registered OpenRouter image models |
| `OpenAIImageGenerator` | Official OpenAI image generation and multipart edits with one or more reference images |
| `VolcengineImageGenerator` | Ark/Seedream generation with bounded data-URL references, explicit size, `stream=false`, and `watermark=false` by default |
| `ComfyUiImageGenerator` | Trusted local ComfyUI workflow submission, progress, history, and bounded output retrieval |
| `LocalAiMediaGenerator` | LocalAI-compatible image, speech, and video endpoints |

Adapters use the shared authentication boundary or an explicitly trusted local endpoint. Remote plaintext HTTP is rejected; loopback HTTP must be explicitly allowed. Credentials, prompts, provider response bodies, and source base64 are excluded from public errors and metadata.

OpenAI image sizes are limited to values accepted by that API. The adapter does not invent a `2048x1152` request or distort an output. Seedream may accept an explicit `2048x1152` request. Canvas fitting, letterboxing, cropping policy, engine compression, and texture import remain game-owned deterministic transforms.

## Recommended flow

```text
Agent calls a visible media Tool
  -> host derives stable operation ID and exact expected world revision
  -> durable job records generation dispatch
  -> selected provider returns bounded binary output
  -> content-addressed resource store validates and persists bytes
  -> durable job records the manifest
  -> game-owned importer validates and commits on the engine thread
  -> authoritative receipt settles the job
```

Do not treat a model-returned URL as a finished asset and do not load untrusted bytes directly into a renderer, audio decoder, or video decoder. Validate origin, redirects, size, MIME and magic bytes, dimensions/duration, storage quota, content policy, licensing, and the current save generation before import.

Provider generation and engine import are external side effects. Cancellation or a lost response after dispatch becomes an explicit uncertain state. Recovery must use provider evidence or the game's stable operation ledger; it must not blindly repeat a charged generation or world mutation.
