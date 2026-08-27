# Durable image attachments

OpenGameAgent keeps image bytes out of the canonical conversation transcript. The optional `@opengameagent/attachments` package admits PNG, JPEG, GIF, and WebP observations into a bounded, content-addressed local store. A transcript stores only an immutable `imageRef` containing the content hash, MIME type, byte count, and dimensions.

```ts
import { LocalGameImageAttachmentStore } from "@opengameagent/attachments";
import { PiGameAgentKernel } from "@opengameagent/kernel-pi";

const imageAttachments = new LocalGameImageAttachmentStore({
  directory: "D:/my-game/save-data/agent-images",
  maximumBytes: 16 * 1024 * 1024,
  maximumPixels: 32 * 1024 * 1024,
});

const kernel = new PiGameAgentKernel({
  models,
  conversationStore,
  imageAttachments,
});
```

An initial `GameInput` may contain either an inline canonical Base64 image or a previously admitted `imageRef`. When the attachment store is configured, inline images are converted to references before the transcript is saved. Before a model request, each reference is resolved and its hash and metadata are verified; missing, corrupt, or substituted content fails closed before the provider is called.

The local store:

- uses SHA-256 content identifiers and atomic directory publication;
- deduplicates identical concurrent admission;
- rejects unsupported formats, MIME mismatches, oversized bytes, oversized pixel dimensions, malformed bounded headers, partial state, content corruption, and redirected storage paths;
- never performs an implicit network request or model download;
- returns attachment metadata, not image bytes, from the public transcript projection.

Inline images remain available for one-shot kernels without an attachment store. Exact live `steer` and `followUp` control messages are synchronous and therefore accept inline images only; resolve durable references before issuing those control calls. Initial runs and persistent transcript restoration support references directly.

Image attachment storage is derived content, not game authority. The game still decides which screenshots or observations a character may see and must authorize any endpoint that retrieves the underlying bytes.
