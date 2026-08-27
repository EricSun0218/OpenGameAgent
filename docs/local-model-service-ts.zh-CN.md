# 本地模型服务

OpenGameAgent 的本地模型和云端模型使用同一套 Agent 循环。`@opengameagent/kernel-pi` 已提供 Ollama、LM Studio、LocalAI、llama.cpp、vLLM 的受信模型 profile，并提供可选的本地服务生命周期层。

`LocalGameModelCatalogClient` 只从固定 loopback 端点读取有界模型目录，支持 OpenAI-compatible `/v1/models`、Ollama `/api/tags` 和 llama.cpp router 状态。模型不能选择或修改端点。

```ts
import { LocalGameModelCatalogClient, createLocalGameModelPreset } from "@opengameagent/kernel-pi";

const catalog = new LocalGameModelCatalogClient({ backend: "ollama" });
const models = await catalog.list();

const preset = createLocalGameModelPreset({
  backend: "ollama",
  model: models[0]!.id,
  input: ["text", "image"],
});
```

`LocalGameModelService` 会先探测已经运行的本地服务。服务不可用时，宿主可以传入 `LocalGameModelProcessController`；OGA 只启动一次，并等待同一个有界模型目录就绪，同时记录该进程是否由自己启动。内置 `SpawnedGameLocalModelProcess` 使用精确可执行文件启动，不经过 shell，在 Windows 上隐藏进程窗口，并丢弃子进程输出，避免模型服务日志进入 Agent 诊断。

```ts
import {
  LocalGameModelService,
  SpawnedGameLocalModelProcess,
} from "@opengameagent/kernel-pi";

const service = new LocalGameModelService({
  backend: "llama.cpp",
  process: new SpawnedGameLocalModelProcess({
    executable: "D:/models/llama-server.exe",
    arguments: ["--host", "127.0.0.1", "--port", "8080"],
  }),
});

const ready = await service.start();
```

框架不内置模型权重，也不会自动下载模型。游戏自行决定随包分发哪些模型，或要求玩家安装哪些模型。发现响应、模型数量、超时、启动参数和端点都有明确上限；远程地址会被拒绝，云端模型应通过普通的受信 Provider 目录接入。
