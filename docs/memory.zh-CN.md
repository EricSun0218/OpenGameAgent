# 混合与向量记忆

[English](memory.md)

`OpenGameAgent.Memory` 是可选的语义记忆包。游戏的
`IGameMemoryStore` 始终是权威数据，向量只是可重建的派生索引。框架不
内置或下载模型权重；宿主可提供任意本地/远程
`IMemoryEmbeddingProvider`，也可选择 `OpenGameAgent.Memory.Onnx`，在
进程内运行 BGE-M3 INT8，无需 Python 或本地模型服务。

## 源码引用与组合

```xml
<ItemGroup>
  <ProjectReference Include="..\OpenGameAgent\src\OpenGameAgent\OpenGameAgent.csproj" />
  <ProjectReference Include="..\OpenGameAgent\src\OpenGameAgent.Persistence\OpenGameAgent.Persistence.csproj" />
  <ProjectReference Include="..\OpenGameAgent\src\OpenGameAgent.Memory\OpenGameAgent.Memory.csproj" />
  <!-- 仅在使用官方进程内 BGE-M3 适配器时添加。 -->
  <ProjectReference Include="..\OpenGameAgent\src\OpenGameAgent.Memory.Onnx\OpenGameAgent.Memory.Onnx.csproj" />
</ItemGroup>
```

权威存储、派生向量目录、嵌入器与游戏时间重排器可独立替换：

```csharp
var authoritative = new FileGameMemoryStore(saveMemoryDirectory);
await authoritative.MigrateLegacyLayoutAsync(cancellationToken);

var vectorIndex = new FileVectorMemoryIndex(derivedVectorDirectory);
await vectorIndex.MigrateLegacyIndexAsync(cancellationToken);

await using var memory = new VectorMemoryStore(
    authoritative,
    vectorIndex,
    embeddings,
    reranker: new GameAwareMemoryReranker(),
    diagnostics: diagnosticSink);

var results = await memory.SearchAsync(
    new GameMemoryQuery(
        sessionId,
        limit: 8,
        ownerId: npcId,
        text: playerQuery,
        atOrBefore: currentGameMoment),
    cancellationToken);
```

更换模型权重、量化、Tokenizer、Pooling、维度或预处理时必须改变
`MemoryEmbeddingIdentity`。旧向量会进入 `RebuildRequired`，不会与新向量
静默混用。`RuntimeMemoryLifecycle` 提供检查、显式重建和 Provider 生命周期
管理。嵌入失败默认只降低为词法召回，不会丢失权威记忆。

## 进程内 BGE-M3 INT8

`OpenGameAgent.Memory.Onnx` 正式支持 Xenova 风格的 BGE-M3 XLM-RoBERTa
目录：

```text
BgeM3/
  config.json
  tokenizer_config.json
  sentencepiece.bpe.model
  onnx/
    model_int8.onnx
```

宿主传入本地只读目录和可选 SHA-256 清单。Provider 会校验文件、模型与
Tokenizer 配置，执行 XLM-R ID 映射、BOS/EOS、attention mask、CLS pooling
和 L2 归一化，输出 1024 维向量。队列、并发、批大小、Token 数、超时、
取消、张量内存和文件大小都有上限；不会网络回退，也不会记录输入原文。

## 分区文件存储与旧布局迁移

`FileGameMemoryStore` 按哈希后的 `sessionId/ownerId` 持久分区。查询同时给出
这两个身份时，只枚举目标 owner 分区；只给 session 时，也只枚举该
session。查询复杂度因此随相关身份分区增长，而不是随所有存档/NPC 的总
记忆数增长。

旧版根目录下的 `*.memory.json` 会在首次访问时自动迁移。正式宿主应在
允许 Agent 运行前显式调用 `MigrateLegacyLayoutAsync`，把一次性磁盘工作
从 NPC 首次上下文构建中移出。迁移会先校验旧文档，再在同一存储内原子
重命名；它跨进程串行、可在取消或崩溃后恢复，并对损坏或身份冲突
fail-closed。新写入使用版本化总数与 pending journal，容量判断无需全库
扫描。路径及枚举到的文件/目录都会拒绝符号链接和 reparse point。

升级前仍应备份存档：新 Runtime 能读取并迁移旧扁平布局，但旧 Runtime
不认识新分区布局，不是受支持的回退读取器。`FileVectorMemoryIndex` 是派生
数据，它保留原向量文件并建立小型分区标记；迁移可重复运行，必要时也可
删除后显式重建。

## 单次权威快照

`IGameMemorySearchSnapshotSource` 可在一次有界读取中返回词法结果和产生
这些结果所用的确切 session/owner 权威快照。`VectorMemoryStore` 会复用
这份快照校验派生向量，不再为同一查询第二次扫描权威存储。
`IGameMemoryPartitionSnapshotSource` 与 `IVectorMemoryPartitionIndex` 为自定义
存储/索引提供 owner 级优化能力；它们不会改变授权、可见性或权威边界。

## 分段观测

`GameMemorySearchSnapshot.Stages` 会分别报告：存储迁移、权威快照、词法
搜索、向量索引读取、嵌入、向量打分和重排。每段只含耗时、扫描数、候选
数和是否复用权威快照，不含查询、记忆正文、身份、凭证或隐藏推理。

同时安装 `GameMemoryExtension` 与 `GameAgentTracingExtension` 后，trace 会有
`memory.search.completed`；每个宿主/扩展上下文 Provider 还会产生
`context.provider.completed`，包含名称、`initial`/`refresh` 阶段、切片数、
耗时和可选扩展 ID。`GameAgentPerformanceSummary.ContextProviders` 与
`MemorySearchStages` 可直接区分宿主上下文、词法读取、向量索引、Embedding
和 Reranker 的成本。

## 10k 可复现基准

```powershell
dotnet run --project benchmarks/OpenGameAgent.Memory.Benchmarks/OpenGameAgent.Memory.Benchmarks.csproj -c Release -- --entries 10000
```

该工具创建跨多个 session/owner 的 10,000 条旧布局记忆，测量旧式全目录
扫描、真实一次性迁移和 7 次热 owner 查询，并输出 JSON。2026-08-23 的一
次 Windows/.NET 8 本机结果为：旧扫描 2016.4ms；一次性崩溃安全迁移
33376.7ms；迁移后只扫描相关 8 条的查询中位数 3.47ms，重复查询延迟约
降低 581 倍。此数据只用于说明复杂度变化，不是跨硬件性能承诺；目标机器
上工具输出的 JSON 才是权威结果。

## 验证

```powershell
dotnet test tests/OpenGameAgent.Persistence.Tests/OpenGameAgent.Persistence.Tests.csproj -c Release
dotnet test tests/OpenGameAgent.Memory.Tests/OpenGameAgent.Memory.Tests.csproj -c Release
dotnet test tests/OpenGameAgent.Memory.Onnx.Tests/OpenGameAgent.Memory.Onnx.Tests.csproj -c Release
dotnet test OpenGameAgent.sln -c Release
```
