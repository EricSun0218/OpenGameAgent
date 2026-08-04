# Bounded lexical memory

The runtime includes two local memory-search modes that do not require an
embedding service:

- `DeterministicMemoryStore` preserves the original lexical overlap behavior.
- `Bm25MemoryStore` ranks content and tags with deterministic BM25F scoring.

Both stores apply the same `MemoryQuery` visibility filters before returning a
record. Scope, required tags, expiry, world, session, save revision, committed
provenance, timeline, game time, and observer incarnation are therefore
independent of the selected ranking algorithm.

## Deterministic tokenization and bounds

`DeterministicUnicodeTokenizer` applies NFKC normalization and invariant
lowercasing. Unicode letters and digits form word terms. Han, Hiragana,
Katakana, and Hangul text also produces code-point unigrams and adjacent
bigrams, allowing short CJK queries to recall longer text.

Tokenizer input bytes, text segments, term bytes, total terms, and unique terms
are hard bounded. `Bm25MemoryStoreOptions` additionally bounds source bytes per
document, aggregate index bytes, aggregate index terms, query terms, and
term/document comparisons.
Crossing a bound throws `LexicalSearchLimitException` with a stable reason
code. BM25 floating-point results are quantized to integers before results are
compared; equal scores use update time and then ordinal memory ID.

## Persistent opt-in

`FileMemoryStore` continues to use deterministic lexical search by default.
BM25F can be selected explicitly:

```csharp
var options = new FileMemoryStoreOptions
{
    SearchMode = FileMemorySearchMode.Bm25,
    Bm25Options = new Bm25MemoryStoreOptions()
};

await using var memories = new FileMemoryStore("memory.log", options);
```

The append-only journal remains the source of truth. On startup, committed
frames are validated and replayed first; the selected in-process index is then
rebuilt from that recovered state. The index itself is not a second durable
store.

`IndexDiagnostics` exposes the index identity and version, tokenizer identity
and version, recovered source revision, and current status. These values are
read-only and do not disclose memory contents.

Atomic and idempotent batches update the selected index only after the journal
commit succeeds. An index-application failure faults the file store so callers
cannot continue against a state whose durable log and in-process index may
differ.

## Deferred tool search

Authorized deferred-tool search uses bounded BM25F fields for tool name,
description, toolset, and parameter names. Exact identity and name-substring
boosts remain in place. Search does not change disclosure policy: only tools
already authorized for search, model activation, and revalidation are ranked,
and activation still requires the exact name, version, and descriptor digest.
