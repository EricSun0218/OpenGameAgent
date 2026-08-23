# Hybrid and vector memory

[中文](memory.zh-CN.md)

`OpenGameAgent.Memory` is an optional package for semantic memory. It keeps the
game's `IGameMemoryStore` authoritative and treats vectors as a rebuildable
derived index. It does not ship model weights. Games can supply any local or
remote `IMemoryEmbeddingProvider`, or add the optional
`OpenGameAgent.Memory.Onnx` package for an in-process BGE-M3 INT8 runtime.

## Local source references

For a Godot 4.7 .NET game consuming a checked-out framework repository, add
these references to the game project (adjust the relative path):

```xml
<ItemGroup>
  <ProjectReference Include="..\OpenGameAgent\src\OpenGameAgent\OpenGameAgent.csproj" />
  <ProjectReference Include="..\OpenGameAgent\src\OpenGameAgent.Persistence\OpenGameAgent.Persistence.csproj" />
  <ProjectReference Include="..\OpenGameAgent\src\OpenGameAgent.Memory\OpenGameAgent.Memory.csproj" />
  <!-- Add only when using the official in-process BGE-M3 adapter. -->
  <ProjectReference Include="..\OpenGameAgent\src\OpenGameAgent.Memory.Onnx\OpenGameAgent.Memory.Onnx.csproj" />
</ItemGroup>
```

`OpenGameAgent.Memory` already references the shared runtime. The explicit
runtime reference is still recommended because game code normally constructs
`GameAgentRuntime`, `GameInput`, and tools directly. Add provider or engine
projects only when the game actually uses them. Do not reference binaries from
the pre-rewrite `GameAgent.*` architecture; current namespaces are
`OpenGameAgent.*`.

## Supply a local embedding model

Implement `IMemoryEmbeddingProvider` around a game-owned in-process runtime or
localhost sidecar. BGE-M3 is one valid choice, but the framework never assumes
its transport or preprocessing. Query and document entry points are separate
because some embedding models use different task prefixes or modes.

```csharp
using OpenGameAgent.Memory;

public sealed class LocalBgeM3Embeddings : IMemoryEmbeddingProvider
{
    private readonly ILocalEmbeddingClient _client;

    public LocalBgeM3Embeddings(ILocalEmbeddingClient client) => _client = client;

    // Change Version whenever weights, quantization, dimensions, pooling, or
    // preprocessing changes. Existing vectors will then require a rebuild.
    public MemoryEmbeddingIdentity Identity { get; } =
        new("local", "bge-m3", "weights-v1-preprocess-v1", 1024);

    public ValueTask<ReadOnlyMemory<float>> EmbedQueryAsync(
        string text,
        CancellationToken cancellationToken) =>
        _client.EmbedAsync(text, isQuery: true, cancellationToken);

    public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedDocumentsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken) =>
        _client.EmbedBatchAsync(texts, isQuery: false, cancellationToken);

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
```

## In-process BGE-M3 INT8

`OpenGameAgent.Memory.Onnx` is an optional provider package. It runs embedding
in the host process through ONNX Runtime, implements
`IMemoryEmbeddingProvider`, and does not require Python or a localhost model
service. It never downloads a model and performs no network fallback. The game
installer owns the weights and passes a local, read-only directory with this
layout:

```text
BgeM3/
  config.json
  tokenizer_config.json
  sentencepiece.bpe.model
  onnx/
    model_int8.onnx
```

The supported contract is the Xenova-style BGE-M3 XLM-RoBERTa export with a
1024-dimensional `last_hidden_state`. The provider applies the XLM-R fairseq ID
mapping, BOS/EOS tokens, attention masks, CLS pooling, and L2 normalization.
Query and document methods intentionally use the same BGE-M3 preprocessing.

```csharp
using OpenGameAgent.Memory;
using OpenGameAgent.Memory.Onnx;

var options = new BgeM3OnnxEmbeddingOptions(modelDirectory)
{
    MaximumTokens = 512,
    MaximumBatchSize = 8,
    MaximumConcurrentInferences = 1,
    MaximumQueuedOperations = 8,
    QueueTimeout = TimeSpan.FromSeconds(3),
    InferenceTimeout = TimeSpan.FromSeconds(20),
    ExpectedSha256 = new Dictionary<string, string>
    {
        ["onnx/model_int8.onnx"] = trustedModelSha256,
        ["sentencepiece.bpe.model"] = trustedTokenizerSha256,
    },
    Metrics = embeddingMetrics,
};

await using var embeddings =
    await BgeM3OnnxEmbeddingProvider.CreateAsync(options, cancellationToken);

await using var memory = new VectorMemoryStore(
    authoritativeMemoryStore,
    rebuildableVectorIndex,
    embeddings,
    reranker: new GameAwareMemoryReranker(),
    diagnostics: memoryDiagnostics);
```

At load time the provider requires the four files, checks bounded sizes,
computes SHA-256 for the model manifest, validates the model/tokenizer config,
and optionally compares host-supplied sizes and hashes. The resulting
`MemoryEmbeddingIdentity` includes the weights, tokenizer, preprocessing, and
maximum-token identity, so a change produces `RebuildRequired` instead of
silently mixing vectors.

Inference is dispatched away from the caller thread. Concurrency, queue depth,
queue wait, batch size, batch token count, tensor allocation estimate, model
size, load time, inference time, and cancellation are bounded. Structured
metrics report operation type, cold load, queue/tokenization/inference timing,
batch counts, token counts, truncation, and failure category without recording
input text.

For a Windows x64 self-contained host, publish the consuming application in the
normal way; the package carries the ONNX Runtime native asset transitively:

```powershell
dotnet publish path/to/Host.csproj -c Release -r win-x64 --self-contained true
```

Keep the model directory outside the authoritative save. It is an application
asset, not a memory index, and can be shared by many saves. When model loading
or inference is unavailable, `VectorMemoryStore` retains its existing lexical
fallback behavior unless the host explicitly configured semantic recall as
mandatory.

Compose the authoritative save store, derived vector directory, lexical/vector
fusion, game-time reranker, and diagnostics:

```csharp
var authoritative = new FileGameMemoryStore(saveMemoryDirectory);
var memory = new VectorMemoryStore(
    authoritative,
    new FileVectorMemoryIndex(derivedVectorDirectory),
    new LocalBgeM3Embeddings(localClient),
    reranker: new GameAwareMemoryReranker(),
    diagnostics: diagnosticSink);

var status = await memory.GetStatusAsync(sessionId, cancellationToken);
if (status.RequiresRebuild)
{
    status = await memory.RebuildAsync(sessionId, cancellationToken);
}

var results = await memory.SearchAsync(
    new GameMemoryQuery(
        sessionId,
        limit: 8,
        ownerId: npcId,
        text: playerQuery,
        atOrBefore: currentGameMoment),
    cancellationToken);
```

Use this `memory` instance anywhere an `IGameMemoryStore` is accepted, including
`GameMemoryExtension`. `RuntimeMemoryLifecycle` is a small optional owner for
inspection, explicit rebuild, and provider disposal.

## Partitioned file storage and migration

`FileGameMemoryStore` persists authoritative records under hashed
`sessionId`/`ownerId` partitions. A query with both identities enumerates only
that owner partition; a session-only query enumerates only that session. The
store therefore scales with the relevant identity partition instead of every
memory in every save or actor.

Flat `*.memory.json` stores written by the previous layout are migrated
automatically on first access. Production hosts should perform the same
idempotent migration before admitting Agent runs, so one-time disk work is not
charged to an NPC's first context build:

```csharp
var authoritative = new FileGameMemoryStore(saveMemoryDirectory);
var migration = await authoritative.MigrateLegacyLayoutAsync(cancellationToken);
Console.WriteLine($"Migrated {migration.MigratedEntries}; total {migration.PartitionedEntries}");

var vectors = new FileVectorMemoryIndex(derivedVectorDirectory);
var vectorEntries = await vectors.MigrateLegacyIndexAsync(cancellationToken);
```

Migration validates every legacy document before an atomic same-store rename,
is serialized across processes, resumes safely after cancellation or a crash,
and fails closed on corrupt or conflicting identities. New appends use a
versioned count plus a pending-mutation journal, so capacity remains bounded
without rescanning the store. Paths and every enumerated file/directory reject
symbolic links and reparse points. Keep a save backup before upgrading: the new
runtime reads and migrates the flat layout, but an older runtime does not know
the partitioned layout and is not a supported rollback reader.

`FileVectorMemoryIndex` keeps its derived vector records in their existing
location and builds small hashed partition markers. Its migration can always be
rerun because vectors remain disposable derived data.

## Single-snapshot hybrid recall

`IGameMemorySearchSnapshotSource` optionally returns lexical results together
with the exact bounded authoritative session/owner snapshot used to produce
them. `VectorMemoryStore` consumes that capability when available, validates
all derived candidates against the same snapshot, and does not enumerate the
authoritative store a second time. `IGameMemoryPartitionSnapshotSource` and
`IVectorMemoryPartitionIndex` provide owner-bounded fallback reads for custom
stores and indexes. These are optimization capabilities, not alternate
authority or visibility rules.

## Search observability

`GameMemorySearchSnapshot.Stages` reports bounded operational metrics for
storage migration, authoritative snapshot, lexical search, vector-index read,
embedding, vector scoring, and reranking. Each stage contains only duration,
scanned/candidate counts, and whether an authoritative snapshot was reused; it
contains no query text, memory content, identifiers, credentials, or hidden
model reasoning.

When `GameMemoryExtension` and `GameAgentTracingExtension` are installed, the
same values appear as `memory.search.completed`. Every host or extension
context provider also emits `context.provider.completed` with its stable name,
phase (`initial` or `refresh`), slice count, duration, and optional extension
ID. `GameAgentPerformanceSummary` exposes these as `ContextProviders` and
`MemorySearchStages`, so a slow host context builder, lexical read, vector
index, embedding call, or reranker can be separated without recording content.

## Save and failure boundaries

- `FileGameMemoryStore` is authoritative save data. It is written before any
  embedding call.
- `remember_game_memory` is an idempotent state-changing tool, not part of the
  conversation-store transaction. Its generated ID is a versioned digest of
  session, actor, input, game moment, turn, and tool position, so retrying the
  same logical call reuses the same memory and conflicting content fails
  closed. A host that needs memory and world state in one atomic transaction
  should expose its own durable game action and append memory in that
  authoritative transaction.
- `FileVectorMemoryIndex` is derived data. Keep it outside the authoritative
  save directory. It contains memory text and metadata along with vectors, but
  never credentials.
- Search verifies every vector candidate against the authoritative snapshot.
  Orphaned or mismatched derived records fail closed and cannot enter context.
- Embedding failure emits a structured `MemoryVectorDiagnostic` and normally
  falls back to lexical recall. Set `FailWhenEmbeddingUnavailable` only when
  semantic recall is mandatory.
- Changing `MemoryEmbeddingIdentity` makes existing vectors stale. The runtime
  excludes them and reports `RebuildRequired`; it never silently mixes models.
- Rebuild is explicit, bounded, resumable by rerunning, and removes derived
  records that no longer exist in the authoritative save. Run an explicit
  rebuild while writes for that session are quiescent. A concurrent append is
  never lost from the authoritative store, but it can leave the derived index
  in `RebuildRequired` state and require one more rebuild.
- The framework normalizes vectors and applies reciprocal-rank fusion before an
  optional game-aware reranker. Game time, not wall-clock time, controls the
  included reranker's recency signal.

## Verification

```powershell
dotnet test tests/OpenGameAgent.Memory.Tests/OpenGameAgent.Memory.Tests.csproj -c Release
dotnet test tests/OpenGameAgent.Memory.Onnx.Tests/OpenGameAgent.Memory.Onnx.Tests.csproj -c Release
dotnet test OpenGameAgent.sln -c Release
```

The opt-in scale benchmark creates 10,000 legacy records across many
session/owner identities, compares an old-layout full-directory scan, performs
the real migration, and measures seven hot owner queries:

```powershell
dotnet run --project benchmarks/OpenGameAgent.Memory.Benchmarks/OpenGameAgent.Memory.Benchmarks.csproj -c Release -- --entries 10000
```

One Windows/.NET 8 run on 2026-08-23 measured 2,016.4 ms for the repeated
full-directory scan, 33,376.7 ms for the one-time crash-safe migration, and a
3.47 ms median partitioned query that scanned exactly the eight relevant
records (about 581x lower repeated query latency). This is a reproducible
example, not a hardware-independent performance guarantee; use the emitted
JSON on the target machine as the authoritative result.

Set `OGA_BGE_M3_MODEL_DIR` to a compatible local model directory to include the
real-weight smoke test. Model files are never copied into the repository or
test output.
