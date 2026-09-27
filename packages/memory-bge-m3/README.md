# @opengameagent/memory-bge-m3

Optional local BGE-M3 embedding adapter for OpenGameAgent memory. It runs ONNX inference in a dedicated Node.js worker
through ONNX Runtime Web's WebAssembly backend, leaving the game loop responsive. The adapter is not a default model and
the rest of OpenGameAgent does not depend on it.

The host supplies the ONNX model, `tokenizer.json`, `tokenizer_config.json`, and `config.json` together with trusted SHA-256
digests. Model assets are never bundled or downloaded by this package. The host also supplies batch, UTF-8 byte, token,
timeout, and pending-request limits. BGE-M3's output dimension is fixed at 1024 and its upstream token limit is 8192. The
Node.js WebAssembly execution provider is single-threaded.

Cancelling or timing out one embedding job rejects that caller immediately without terminating the model worker. ONNX
inference itself is not interruptible, so the provider keeps the cancelled request ID as an isolation fence, discards
its eventual result, and dispatches the next queued job only after that result arrives. This preserves the warm model
and prevents cross-request result contamination. Artifact or model-contract failures are terminal for that provider
instance and do not expose local paths in public errors.

## Local resource benchmark

Run `npm run benchmark:bge-m3 -- --config <path>` after building the workspace. The JSON configuration must explicitly
provide pinned artifact paths and digests, run count, warm iteration count, RSS sampling interval, request timeout,
whole-case timeout, whether to sample Windows process memory, and one or more `{ batchSize, tokenCount }` cases. The
harness intentionally supplies no numeric defaults: benchmark procedure and workload remain host decisions.

Every measured run uses a fresh child process. The report records first-embed and warm latency, cumulative whole-process
maximum RSS, sampled RSS and Windows Private Bytes, main-thread Node memory counters, input/output sizes, dependency
versions, model hashes and sizes, host characteristics, revision, and working-tree state. Generated text is verified to
encode to the requested token count with the expected special tokens before inference. When Windows process sampling is
enabled, measured latency includes that observer's overhead and the report includes its sample count.

Derived byte fields cover the two int64 input tensors, the float32 `[batch, tokens, 1024]` output tensor, returned dense
vectors, and the known ONNX Runtime input/output copy floor. They do not include model/session storage, tokenizer data,
WebAssembly heap, graph intermediates, or attention workspace. Results describe the measured host and must not be treated
as portable production limits.
