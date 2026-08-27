# 制品与外部知识

`@opengameagent/artifacts` 用于把大型工具结果和游戏拥有的知识移出当前模型上下文，同时不会复制第二套 Agent 循环。

## Agent 制品

`createGameArtifactResources` 提供两个应当一起安装的资源：

- 工具执行中间件：把过大的文本或 JSON 结果替换为有界预览和稳定制品引用；
- `read_agent_artifact` 工具：只能在创建制品时对应的 world、save、timeline、generation、owner、session、actor 中分页读取。

内置的 `SqliteGameArtifactStore` 可完全本地、自托管运行。相同 input、run、turn、工具调用和结果会得到稳定的内容派生 ID，重试存储不会创建另一份制品。如果工具已经执行完成但可选制品存储失败，框架会返回原始工具结果，避免模型重复执行可能修改世界的工具。

```ts
import { createGameArtifactResources, SqliteGameArtifactStore } from "@opengameagent/artifacts";

const artifactStore = new SqliteGameArtifactStore("./save/agent-artifacts.db");
const artifacts = createGameArtifactResources({ store: artifactStore });

// 将 artifacts.toolProvider 加入运行时工具提供器。
// 将 artifacts.execution 加入工具执行中间件链。
```

制品存储不是游戏权威世界状态的替代品。它适合保存不可变的模型可见输出，例如报告、检查结果和检索文档。

## 外部知识

`createExternalKnowledgeToolProvider` 只暴露宿主预先注册的知识源。模型只能选择知识源 ID 并提交结构化查询，不能指定端点或凭据。

```ts
import { createExternalKnowledgeToolProvider, JsonHttpGameKnowledgeSource } from "@opengameagent/artifacts";

const knowledge = createExternalKnowledgeToolProvider({
  artifactStore,
  sources: [
    new JsonHttpGameKnowledgeSource({
      id: "world-lore",
      endpoint: "http://127.0.0.1:7777/query",
    }),
  ],
});
```

远程 HTTP 知识源必须使用 HTTPS，本机 sidecar 可以使用 loopback HTTP。框架拒绝重定向和带 URL 凭据的地址。默认不会把玩家输入和游戏上下文转发给知识源，只有宿主显式开启后才会发送上下文。响应大小、条目数量、元数据和内联结果都有明确上限；大型结果会转成会话隔离的制品。

两类资源都会在执行时重新校验精确会话，因此宿主误保留旧工具实例时也不能跨角色或跨存档访问。
