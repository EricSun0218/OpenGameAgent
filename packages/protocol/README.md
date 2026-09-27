# @opengameagent/protocol

Canonical versioned data and runtime event schemas for OpenGameAgent.

Owner-visible assistant messages may include ordered `reasoning-summary` parts when an upstream provider explicitly
classifies that text as a summary. Each part retains its source position alongside ordinary assistant text. Raw
reasoning, redacted content, provider replay envelopes, signatures, and response identifiers are never canonical event
content.

Owner-visible message content may include immutable `image-reference` records. These records contain only a
content-addressed identifier and verified display metadata; binary image data is deliberately outside the canonical
event stream. An internal context message contains exactly one canonical text part followed by zero or more name-free
image references that prove the exact projection associated with its context snapshot; raw Pi image data, additional
text parts, local paths, and remote URLs remain invalid canonical event content.

Durable transcript schema version 6 distinguishes completed user/assistant messages, deferred provider operations, Tool
execution starts, Tool results, turn closures, and context checkpoints. A deferred operation stores its opaque provider
handle and normalized game input outside model-visible history. Exactly one may remain open in a turn. Its completion
must link to that opening record and provider origin before the final assistant message enters the normal Tool loop; an
explicit abandonment ends the local operation without inventing provider output. User part arrays and Tool-result content may retain the same immutable
`image-reference` records used by owner-visible messages; the user text format remains exactly one text part. Assistant
content retains ordered thinking, text, and Tool calls. An optional versioned Pi replay envelope supplements that durable
content with provider continuation fields; its source identity, stop reason, block count, and block types must align
exactly, and it never overrides visible content or Tool identity. Tool records link to the exact assistant record and
content position. A result explicitly states whether execution completed, never started, or has an unknown outcome;
sequence order remains the storage authority. A `completed` closure requires a terminal assistant message, while an
`interrupted` closure may end an input-only prefix repaired after a crash. A context checkpoint must immediately follow
the completed closure it names and contains one complete replacement message history. Restore keeps validating the raw
prefix, replaces the model projection at each valid checkpoint, and then folds the later canonical suffix. Checkpoints
use their dedicated compare-and-append API; the generic transcript commit surface cannot create them.
