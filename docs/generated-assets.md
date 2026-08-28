# Generated assets and authoritative import

`@opengameagent/media` separates provider generation from authoritative game import. `GameMediaRegistry` selects and bounds a provider. `DurableGameMediaPipeline` records a stable operation before either external side effect, stores validated outputs as content-addressed resources, and requires a game-owned importer receipt before reporting completion.

Use this path when an image, voice line, video, or other generated resource must survive restart and become part of a save. Media generation does not create a second Agent loop and never grants permission to modify the world.

## Lifecycle

```text
prepared
  -> generating
  -> generated
  -> importing
  -> completed | rejected | failed
```

The two uncertain states are deliberate:

- `generation-uncertain` means the provider may have accepted or charged the request. Replaying `execute` returns the existing job and never submits it again. Supply externally recovered output through `resolveGeneration`, or explicitly close the job through `failGeneration`.
- `import-uncertain` means the engine mutation may have committed before its receipt was lost. `resumeImport` calls the importer's `reconcile` method with the same stable import operation ID; it never repeats `import`.

Cancellation after dispatch also settles to the matching uncertain state with an independent bounded settlement signal. It does not pretend that an external side effect did not happen.

## Persistence and identity

Use `SqliteGameMediaAssetJobStore` for restart-safe job state and `FileGameMediaResourceStore` for content-addressed bytes. `InMemoryGameMediaAssetJobStore` is suitable for tests.

The request fingerprint binds one operation ID to:

- the complete game session, owner, actor, timeline, and generation;
- asset type, provider, model, importer, and expected world revision;
- generation prompt and parameters;
- the hash, MIME type, kind, name, and size of each reference source.

The durable job stores the fingerprint, not the prompt or source bytes. Provider credentials never enter the job or resource store. Reusing an operation ID with changed input fails closed.

## Importer contract

Implement `GameMediaAssetImporter` in the game host:

```ts
const importer: GameMediaAssetImporter = {
  id: "engine-asset-import",
  async import(context, signal) {
    // Validate save generation, expected revision, permissions, MIME, dimensions,
    // quotas, and engine format; then commit once under context.importOperationId.
    return {
      operationId: context.importOperationId,
      session: context.job.session,
      expectedRevision: context.job.expectedRevision,
      status: "committed",
      stateRevision: 42,
    };
  },
  async reconcile(context, signal) {
    // Query the game operation ledger. Never repeat the original mutation here.
    return readExistingReceipt(context.importOperationId, signal);
  },
};
```

The receipt is accepted only when its operation ID, complete session identity, and expected revision match the prepared job. The game remains responsible for content policy, moderation, licensing, quotas, main-thread scheduling, replication, and final save authority.

## Providers

The catalog-backed image adapter consumes the shared image-model registry and therefore supports its registered providers and models, including OpenRouter image models, without another provider-specific loop. Direct adapters are also available for OpenAI Images, Volcengine Seedream, ComfyUI, and LocalAI-compatible local media. All adapters return the same bounded `GameMediaGenerationResult` consumed by the durable pipeline.
