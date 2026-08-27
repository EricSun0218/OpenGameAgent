# MCP 与便携插件

OpenGameAgent 把外部工具和便携插件包放在 Agent 内核之外。它们由宿主按需组合：游戏可以只用原生工具，也可以连接一个受信 MCP 服务，或安装同时包含 Skill 与 MCP 的复用包，而不需要修改消息—工具循环。

## MCP 工具桥

`@opengameagent/mcp` 提供实现 `GameToolProvider` 的 `GameMcpToolBridge`。外部工具仍走 Runtime 原有的工具收集与执行路径，所以输入级可见性、Schema 预检、工具策略、批准中间件、取消、追踪，以及宿主选用的可靠游戏动作适配器都继续生效。

默认的 `on-demand` 模式只向模型暴露一个有界的 `use_external_game_tool` 代理。模型先检索目录，再查看某个工具的精确定义，最后调用它，避免庞大或频繁变化的工具目录占满每次模型请求。小型、受信目录也可以选择 `direct` 模式。

```ts
import { connectHttpGameMcp, GameMcpToolBridge } from "@opengameagent/mcp";

const externalTools = new GameMcpToolBridge({
  servers: [
    {
      id: "world-tools",
      connect: () => connectHttpGameMcp({
        endpoint: "https://tools.example.com/mcp",
        headers: { Authorization: `Bearer ${credential}` },
      }),
      isVisible: input => input.type === "npc.chat",
    },
  ],
});

// 将 externalTools 加入 GameAgentRuntimeOptions.toolProviders。
```

远端 Schema 必须先编译才能进入模型工具目录；不支持的 Schema 会被排除，并通过有界诊断报告。目录更新采用代际替换：工具变化通知使当前快照失效，连接关闭后在下一次收集时建立新连接。框架不会自动重试工具调用，因为外部调用可能已经产生状态变更。

HTTP 端点必须使用 HTTPS；只有宿主明确允许的本机回环地址可以使用 HTTP。重定向会被拒绝，凭据始终属于宿主传输配置。Stdio 只接受明确的可执行文件和参数数组，不经过 Shell。

## 便携插件包

`@opengameagent/plugins` 可加载已发布的 Agent Plugins 1.0.0 目录格式：根目录 `plugin.json`、`skills/` 下的直接子 Skill，以及可选的 `mcp.json`。

```ts
import { loadPortableGamePlugin } from "@opengameagent/plugins";
import { createGameSkillExtension } from "@opengameagent/skills";

const plugin = await loadPortableGamePlugin("./installed/world-tools", {
  dataDirectory: "./plugin-data",
  httpHeaders: {
    remote: { Authorization: `Bearer ${credential}` },
  },
});

const skillResources = plugin.skills
  ? createGameSkillExtension({ source: plugin.skills })
  : undefined;

// 将 plugin.mcp 与 skillResources?.toolProvider 组合进 toolProviders。
// 将 skillResources?.postToolContextProvider 组合进 postToolContextProviders。
```

插件文件不能注入凭据，宿主 Header 会覆盖包内 Header。只有宿主提供持久插件数据目录时，Stdio 组件才会启用。文件访问被限制在解析后的插件根目录或插件数据根目录内；组件数量和体积均有上限；无效 Skill 或 MCP 条目按最小范围隔离，并保留诊断。

便携包只包含声明式 Skill 和 MCP 连接描述，发现阶段不会执行任意 JavaScript。需要代码扩展的开发者，可以发布普通 TypeScript 包，实现 `GameContextProvider`、`GameToolProvider`、`GamePostToolContextProvider`、策略/中间件或其他 OGA 可选接口，再由游戏宿主显式导入并组合受信代码。

## 权责边界

- OpenGameAgent 负责发现、有界校验、协议适配、模型安全投影，以及接入正常 Runtime 执行链。
- 宿主负责决定信任哪些插件与服务、保管凭据、授予进程/网络权限、设置逐输入可见性，并拥有权威游戏工具。
- MCP 工具不会自动变成可靠游戏动作。会修改世界的能力应暴露或调用宿主的可靠动作适配器，继续使用 operation ID、回执、冲突协调与恢复机制。
