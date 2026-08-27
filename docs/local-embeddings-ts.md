# Local in-process embeddings

`@opengameagent/memory-onnx` is an optional Node.js package that implements `GameMemoryEmbeddingProvider` with a local BGE-M3 INT8 ONNX model. It runs tokenization and inference in persistent worker threads, so the game or sidecar event loop does not execute ONNX work directly. The package never downloads a model and has no network fallback.

The host supplies a read-only model directory with this layout:

```text
model/
├── config.json
├── tokenizer_config.json
├── tokenizer.json
├── sentencepiece.bpe.model
└── onnx/
    └── model_int8.onnx
```

The files must describe a 1,024-dimensional XLM-RoBERTa BGE-M3 encoder with an 8,192-token context. `tokenizer.json` is the executable tokenizer graph used by the JavaScript tokenizer; `sentencepiece.bpe.model` is retained and verified as part of the original model artifact. Symlinked roots and ONNX directories, missing files, incompatible metadata, invalid sizes, and configured SHA-256 mismatches fail closed before a worker is allowed to run.

```ts
import { SqliteGameMemoryStore } from "@opengameagent/memory";
import { BgeM3OnnxEmbeddingProvider } from "@opengameagent/memory-onnx";

const embeddings = new BgeM3OnnxEmbeddingProvider({
  modelDirectory: "D:/models/bge-m3",
  manifest: {
    // Change this whenever weights or preprocessing assets change.
    modelVersion: "xenova-main-a206e10e",
    files: {
      "onnx/model_int8.onnx": {
        sha256: "<expected sha256>",
      },
    },
  },
  maximumTokens: 8192,
  maximumBatchSize: 8,
  maximumQueuedBatches: 64,
  concurrency: 1,
  timeoutMilliseconds: 60_000,
});

const memory = new SqliteGameMemoryStore("./save/memory.sqlite", {
  embedding: embeddings,
  // false keeps lexical memory available when a derived embedding cannot be built.
  requireEmbeddingOnWrite: false,
});
```

The provider performs XLM-RoBERTa tokenization, attention-mask mean pooling, and L2 normalization. `embedQuery` and `embedDocuments` share the same BGE-M3 preprocessing; BGE-M3 does not require synthetic `query:` or `passage:` prefixes. Requests are bounded by document count, characters, batch size, queue length, token count, timeout, worker count, and the estimated resident model bytes across workers. Cancelling or timing out in-flight inference terminates that worker and creates a clean worker on the next request.

The embedding identity includes the explicit model version and preprocessing contract. `SqliteGameMemoryStore` stores vectors as a derived index under that identity. After changing the version or preprocessing, create the store with the new provider and call `rebuildEmbeddings(session)` for the affected save boundary. Authoritative memories remain unchanged.

Recall is bounded before authoritative JSON is loaded. Visibility plus optional scope, kind, tag, game-tick, and importance predicates are applied inside both FTS and vector-candidate SQL before `LIMIT`; tag membership uses a normalized index. Hybrid candidates are merged by their lexical/vector relevance and importance before the configured candidate cap. This prevents a long-lived NPC's unrelated recent history from crowding a valid filtered memory out of a small result window.

`GameMemorySearchResult.diagnostics` reports embedding, lexical, vector-candidate, authoritative-load, and rerank time plus candidate counts. It never contains query text or memory content.

Metrics contain only mode, batch size, queue/load/tokenization/inference time, truncation count, and a bounded failure category. They never contain source text, token IDs, model output, credentials, or file bytes.

Run the deterministic tests with `npm test`. To exercise a real model directory:

```powershell
$env:OGA_BGE_M3_MODEL_DIR = "D:\models\bge-m3"
$env:OGA_BGE_M3_MODEL_VERSION = "weights-2026-08-28"
npm test -- --run packages/memory-onnx/src/provider.integration.test.ts
```

OpenGameAgent's current release gates cover Windows and Linux. A packaged game or sidecar must include the matching `onnxruntime-node` native files; players do not need Python or a separate embedding service.
