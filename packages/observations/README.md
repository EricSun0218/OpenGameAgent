# @opengameagent/observations

Optional deterministic visual-observation projection for the Pi-backed OpenGameAgent loop. The package exposes one
entry point, `createVisualObservationContextSource(options)`, which returns an ordinary `GameContextSource`.

The game captures one versioned snapshot for each real model request. Structured scene or calibrated BEV data remains
game-owned JSON. Image candidates refer to already admitted canonical PNG objects. An image can be used unchanged or
cropped and then resized with `inside` fit and no enlargement. Derived PNGs are published through the supplied
`CanonicalPngAttachmentStore`; the in-memory LRU stores only transform keys and name-free attachment references.

Required image reservations are considered before optional candidates, while selected images retain host order.
Optional candidates are omitted only when the configured model projection budget cannot fit them. An omitted candidate
is never authorized, read, decoded, transformed, or published. Authorization, integrity, decode, transform, storage,
and cancellation failures remain failures. A cache hit still authorizes and verifies both the source and the derived
object. Only a precisely missing or corrupt disposable derived object is discarded and rebuilt once; unavailable or
changed storage authority remains a failure.

```ts
import { createVisualObservationContextSource } from "@opengameagent/observations";

const observationSource = createVisualObservationContextSource({
	sourceId: hostSourceId,
	selectionReferenceId: hostSelectionReferenceId,
	capture: captureGameObservation,
	authorize: authorizeImageReference,
	store: canonicalPngStore,
	projectionLimits: hostContextLimits,
	maxCandidatesPerSnapshot: hostCandidateLimit,
	maxTransformCacheEntries: hostCacheEntryLimit,
});
```

Install the same `hostContextLimits` on the `GameAgent` that receives this source. Every count, byte, dimension, and
cache limit is host-required; this package adds no workload defaults, retry, TTL, background refresh, singleflight,
planner, provider converter, or game-state writer.
