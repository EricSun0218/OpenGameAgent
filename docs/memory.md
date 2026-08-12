# Hybrid and vector memory

`OpenGameAgent.Memory` is an optional package for semantic memory. It keeps the
game's `IGameMemoryStore` authoritative and treats vectors as a rebuildable
derived index. The package does not ship an embedding model or model runtime.

## Local source references

For a Godot 4.7 .NET game consuming a checked-out framework repository, add
these references to the game project (adjust the relative path):

```xml
<ItemGroup>
  <ProjectReference Include="..\OpenGameAgent\src\OpenGameAgent\OpenGameAgent.csproj" />
  <ProjectReference Include="..\OpenGameAgent\src\OpenGameAgent.Persistence\OpenGameAgent.Persistence.csproj" />
  <ProjectReference Include="..\OpenGameAgent\src\OpenGameAgent.Memory\OpenGameAgent.Memory.csproj" />
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
dotnet test OpenGameAgent.sln -c Release
```
