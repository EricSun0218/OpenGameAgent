# `@opengameagent/attachments`

Optional verified local image attachments for OpenGameAgent. The package fully decodes admitted PNG, JPEG, WebP, or single-frame GIF input with `sharp`, normalizes it to metadata-free sRGB PNG, publishes immutable SHA-256-addressed objects, verifies every read, and projects authorized bytes into the existing Pi-backed Agent loop while canonical transcripts retain references only.

Every byte, pixel, dimension, name, batch, read, text, and content-part limit is required host policy. The package supplies no media workload defaults. Games remain responsible for attachment authorization and authoritative world/resource writes.

Input projection additionally requires `maxReferenceBytes`, an aggregate budget over the complete canonical JSON
metadata of every selected `image-reference`, including optional names. Selection exceeds this budget before attachment
authorization or storage reads.

`CanonicalPngAttachmentStore` is the narrow publication seam for deterministic derived images. Its
`publishCanonicalPng` method verifies a complete, still, metadata-free PNG against the store's existing encoded-byte,
pixel, and dimension limits, then publishes those exact input bytes under their SHA-256 identity in the same local CAS.
It does not call `admit` or encode the image again. Cancellation and existing storage failure semantics are preserved.

`renderCanonicalPng` is the matching narrow Sharp output helper for optional transform packages. It takes an already
configured Sharp pixel pipeline, streams one fixed PNG encoding under a required host byte budget, propagates abort by
destroying the pipeline, and removes source-density identity from Sharp's physical-pixel chunk. The returned bytes are
accepted unchanged by `publishCanonicalPng`; it is not a renderer abstraction, cache, store, or second media runtime.

`createImageAttachmentTranscriptMedia` adapts an `ImageAttachmentStore` to durable transcript media. It admits all images from a final Tool result as one batch, reads the normalized objects back, and returns Pi content aligned with immutable references so the live request and a later restart use the same bytes. Restore requires a host-supplied `maxRestoreMediaBytes`; the Adapter counts every reference occurrence and rejects an oversized batch before calling either the host authorizer or storage reader. The authorizer receives the transcript stream, message role, and Tool identity when applicable.

The repository-level `benchmark:media` workload exercises an explicitly configured 8192×8192 RGBA input through full
normalization, immutable publication, and cold read. Its checked-in capacities are acceptance-profile inputs only; this
package still supplies no image-size default.
