# 本地进程内嵌入

`@opengameagent/memory-onnx` 是一个可选的 Node.js 包，使用本地 BGE-M3 INT8 ONNX 模型实现 `GameMemoryEmbeddingProvider`。分词和推理在持久 Worker 线程中运行，不会直接占用游戏或 Sidecar 的事件循环。该包不会下载模型，也没有网络回退路径。

宿主提供只读模型目录：

```text
model/
├── config.json
├── tokenizer_config.json
├── tokenizer.json
├── sentencepiece.bpe.model
└── onnx/
    └── model_int8.onnx
```

这些文件必须描述一个上下文长度为 8,192、输出 1,024 维向量的 XLM-RoBERTa BGE-M3 编码器。JavaScript 分词器实际读取 `tokenizer.json`；`sentencepiece.bpe.model` 作为原始模型资产的一部分保留并校验。根目录或 ONNX 目录是符号链接、文件缺失、元数据不兼容、文件大小异常或配置的 SHA-256 不一致时，都会在 Worker 运行前失败。

```ts
import { SqliteGameMemoryStore } from "@opengameagent/memory";
import { BgeM3OnnxEmbeddingProvider } from "@opengameagent/memory-onnx";

const embeddings = new BgeM3OnnxEmbeddingProvider({
  modelDirectory: "D:/models/bge-m3",
  manifest: {
    // 权重或预处理资产变化时必须修改该版本。
    modelVersion: "xenova-main-a206e10e",
    files: {
      "onnx/model_int8.onnx": { sha256: "<预期 SHA-256>" },
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
  // false 表示派生向量暂时不可用时仍可使用词法记忆。
  requireEmbeddingOnWrite: false,
});
```

Provider 完成 XLM-RoBERTa 分词、基于 attention mask 的均值池化和 L2 归一化。`embedQuery` 与 `embedDocuments` 使用同一套 BGE-M3 预处理；BGE-M3 不需要人为添加 `query:` 或 `passage:` 前缀。文档数、字符数、批大小、队列、Token 数、超时、Worker 数以及所有 Worker 的估算常驻模型大小都有明确上限。正在运行的请求被取消或超时后，该 Worker 会被终止，下次请求会创建干净的新 Worker。

嵌入 identity 包含显式模型版本与预处理契约。`SqliteGameMemoryStore` 按该 identity 保存可重建的派生向量索引。更换模型版本或预处理后，用新 Provider 打开存储，并对相应存档边界调用 `rebuildEmbeddings(session)`；权威记忆不会被改写。

在加载权威 JSON 之前，召回候选就已经受到上限约束。可见性以及可选的 scope、kind、tag、游戏 tick 和 importance 条件会在 FTS 与向量候选 SQL 的 `LIMIT` 之前执行，标签使用规范化索引。词法/向量相关性和重要度会先完成混合排序，再应用候选上限，避免长期 NPC 的无关近期历史把真正符合筛选条件的记忆挤出小结果窗口。

`GameMemorySearchResult.diagnostics` 会报告 Embedding、词法查询、向量候选、权威记录加载和重排耗时及候选数量；其中不包含查询原文或记忆正文。

指标只包含模式、批大小、排队/加载/分词/推理耗时、截断数量和有界失败类别，不包含原文、Token ID、模型输出、凭据或模型文件内容。

普通测试运行 `npm test`。使用真实模型目录进行 smoke：

```powershell
$env:OGA_BGE_M3_MODEL_DIR = "D:\models\bge-m3"
$env:OGA_BGE_M3_MODEL_VERSION = "weights-2026-08-28"
npm test -- --run packages/memory-onnx/src/provider.integration.test.ts
```

OpenGameAgent 当前发布门禁覆盖 Windows 与 Linux。打包游戏或 Sidecar 时需要包含匹配平台的 `onnxruntime-node` 原生文件；玩家无需安装 Python，也无需另外启动嵌入服务。
