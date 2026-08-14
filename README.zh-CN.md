<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/brand/opengameagent-mark.svg">
    <source media="(prefers-color-scheme: light)" srcset="docs/brand/opengameagent-mark-light.svg">
    <img src="docs/brand/opengameagent-mark-light.svg" alt="OpenGameAgent OGA monogram" width="112">
  </picture>
</p>

<h1 align="center">OpenGameAgent</h1>

<p align="center"><strong>面向 AI 原生游戏、自主 NPC 与互动世界的开源 Agent Runtime。</strong></p>

<p align="center"><a href="README.md">English</a></p>

OpenGameAgent 是一个紧凑、可修改的 C# Runtime，让游戏角色能够观察结构化状态、制定计划、调用工具、检查权威结果、形成记忆并持续完成目标。它可以运行在 Godot、Unity 或 .NET 服务端中，而每次状态变更仍由游戏代码裁决。

<p align="center">
  <a href="https://github.com/EricSun0218/OpenGameAgent/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/EricSun0218/OpenGameAgent/actions/workflows/ci.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="License: Apache-2.0" src="https://img.shields.io/badge/license-Apache--2.0-blue.svg"></a>
  <a href="CHANGELOG.md"><img alt="Status: alpha" src="https://img.shields.io/badge/status-alpha-orange.svg"></a>
</p>

OpenGameAgent 从小型、可组合的 Agent 内核出发。有状态核心会流式接收模型输出、执行经过校验的工具、在运行中接受 steering，并持续进行模型/工具循环直到任务结束。开发者既可以只使用内核，也可以叠加游戏 Runtime 获得游戏时间与可靠状态，再按需加入记忆、目标、宿主证据校验的任务清单、产物、委派、外部工具、结构化交互和工作流图等扩展。

输入是有大小限制的 JSON，可以表示对话、战斗观察、模拟 Tick、UI 事件、计划、传感状态或任意游戏数据，不要求是自然语言。项目不捆绑模型，同时支持云端和本地 API。

## 为游戏构建的可编程 Agent Runtime

开发者可以选择模型、注册游戏自有工具、组合扩展、订阅流式事件、在运行中 steering，并让模型/工具循环持续执行到任务结束。内核本身可以作为通用 Agent Loop 单独使用。可选的游戏 Runtime 则增加互动模拟真正需要的坐标：会话与角色、游戏时间线与 Tick、结构化世界上下文、多 NPC 有界并发、持久记忆与任务、引擎主线程交接，以及可恢复的动作回执。这些能力通过类型化接口组合，不绑定某一种玩法、题材或世界数据模型。

## 不止对话：观察、决策、行动并持续推进

仅对话角色接收一段提示词，然后返回一句对话。Agent 驱动的角色接收目标和当前环境，选择工具、执行工作、观察结果，并持续行动直到得到明确结果。在 OpenGameAgent 中，这些工具就是普通的游戏业务能力：移动、观察、交易、建造、安排日程、招募、调查，或开发者允许角色使用的任何操作。

| 仅对话角色 | Agent 驱动角色 |
| --- | --- |
| 主要读取最近的对话 | 观察有界 JSON，其中可以包含对话、世界状态、事件、UI 输入、传感数据或模拟 Tick |
| 生成下一句文本 | 流式生成文本和类型化工具调用，并读取结构化工具结果 |
| 一次模型回复后结束 | 可以“观察 → 决策 → 行动 → 检查结果 → 继续”，跨多轮完成任务 |
| 把生成文本本身当作结果 | 只提出动作请求，由游戏代码校验权限、规则、版本和状态变更 |
| 通常依赖现实时间与聊天记录 | 可以使用游戏时间、时间线、存档/会话身份、角色身份和作用域记忆 |
| 一次处理一段对话 | 同一角色串行，不同 NPC 之间有界并发 |
| 超时后重试可能重复写入 | 可以先记录状态变更意图，并在重试前核对游戏返回的权威回执 |

OpenGameAgent 不绑定任何模型或 Provider。角色通过开发者定义的工具行动，模型不能直接控制游戏状态；所有状态变更始终由游戏裁决。

也不是每次互动都必须运行完整 Agent Loop。问候等简单输入可以走快速回复路由，开放式任务使用完整循环，需要固定执行图的场景则可以使用确定性 Workflow。

> 当前版本：`0.3.0-alpha.2`。在 `1.0` 前公开 API 仍可能调整。

内核边界刻意保持小而稳定。后续游戏特有能力通常应通过扩展、工具、策略、工作流或游戏自有服务加入，而不是继续膨胀模型/工具循环。

## 安装

从 NuGet 安装完整的游戏 Runtime：

```bash
dotnet add package OpenGameAgent --version 0.3.0-alpha.2
dotnet add package OpenGameAgent.Memory --version 0.3.0-alpha.2 # 可选语义记忆
```

内核、持久化、模型提供方和引擎兼容客户端也分别提供 `OpenGameAgent.*` 包。Godot、Unity 与可移植服务端压缩包可以从 [Releases](https://github.com/EricSun0218/OpenGameAgent/releases) 页面下载。接入游戏前请阅读[快速开始](docs/getting-started.md)和[引擎接入](docs/engine-integration.md)。

## 为什么要做游戏专用层

通用 Agent 往往默认单用户、现实时间和线性对话。游戏却可能拥有多条时间线、存档分支、成千上万个角色、离线时间跳跃、引擎主线程限制，以及不能在故障后盲目重试的状态变更。

OpenGameAgent 不替游戏规定玩法，而是提供可复用的游戏坐标与执行原语：

- 命名时间线、整数 Tick 和可选日历 JSON；
- 保留浮点数的结构化观察与上下文；
- 快速回复、完整 Agent、确定性 Workflow 三种路由；
- 同一角色串行、不同角色有界并行；
- 先记日志的动作意图与游戏权威回执；
- 按游戏时间过滤、过期并可自定义排序的记忆；
- 可选本地/远程嵌入、可重建向量索引与词法/向量混合召回；
- 根据输入类型和可用工具选择的 Skills；
- 游戏时间触发器，以及支持无 payload 积压查询的持久邮箱；
- 可扩展工具、Skills、路由、Workflow、Hooks、事件与服务的类型化接口；
- 能力感知模型目录与开发者托管的短期凭证；
- 外部工具按需发现与大型结果产物化；
- 包含可移植 Skills 与 MCP Server 的 Agent Plugins 1.0.0 插件包；
- 通过可替换 API 生成图片、语音和视频。

Runtime **不会**判断攻击是否合法、物品能否使用、资源够不够或 NPC 有没有权限。游戏只暴露窄而明确的工具，校验每次变更请求，在正确线程或服务端执行，并返回权威回执。

## 架构

```text
Godot / Unity / .NET 游戏服务
        |
        | GameInput（JSON + GameMoment）
        v
GameAgentRuntime
  上下文 | Skills | 路由 | 会话 | 角色队列 | 扩展
        |
        v
小型有状态 Agent 内核 <---- steering / follow-up
  模型流 -> 工具调用 -> 工具结果 -> 下一轮
        |                         |
        |                         v
        |                    可恢复动作派发器
        |                         |
        v                         v
模型 API                    游戏校验与权威状态
```

只需要精简 Agent Loop 的开发者可以直接使用内核。上层游戏 Runtime 是组合层，不是强制世界数据模型。

## 已实现能力

| 模块 | 能力 |
| --- | --- |
| Agent 内核 | 流式类型化消息、工具循环、类型化工具中间结果、steering、follow-up、hooks、取消、严格会话校验、提供方错误结果化 |
| 工具执行 | provider 请求前 schema 预检及执行期有界 JSON Schema 子集校验、每个已接受调用都有结果、安全并行读、冲突键串行、策略拦截/终止、超时与写入结果未知语义 |
| 游戏 Runtime | 任意 JSON 输入、游戏时钟/时间线、快速/完整/Workflow 路由、乐观并发会话、输入去重、角色并发、运行中 steering/abort |
| 扩展 API | 不可变构建器；提示词/上下文/工具/Skills/路由/Workflow/Hooks/提供方/服务注册；类型化生命周期事件与通道；命名空间持久状态 |
| 官方扩展 | 工具策略与搜索、玩家结构化提问/推荐回复、目标、支持持久暂停/恢复且由宿主校验证据的有序任务清单、记忆、产物、外部知识、委派、追踪和可持久并行工作流图 |
| 世界原语 | 可恢复动作、可续跑 Workflow、记忆、Skills、信号、游戏时间调度、支持批量只读待处理状态的角色邮箱 |
| 模型与认证 | 内置模型能力/上下文/推理级别/成本目录、动态刷新、API Key/环境/存储/OAuth/本地认证、开发者托管短期凭证网关 |
| 外部工具 | 默认按需搜索/描述/调用；小型可信目录可显式选择原生直连暴露 |
| 可移植插件 | [Agent Plugins 1.0.0](docs/agent-plugins.md) `plugin.json`、直接子目录 `SKILL.md` 发现、MCP stdio/Streamable HTTP、客户端命名空间、路径限制与组件级故障隔离 |
| 提供方 | Anthropic、Amazon Bedrock、Google Gemini/Vertex、Mistral、OpenAI Responses/Azure、OpenAI-compatible、远程网关和消息网关；重试与回退包装器 |
| 生成式媒体 | 图片/语音/视频中立注册表、通用异步 HTTP 任务，以及带渐进预览的专用图片适配器 |
| 持久化 | 崩溃安全本地快照、可选追加式会话历史、跨进程协调、动作日志、Workflow 检查点、记忆、邮箱、产物、委派、Skills 与提示词模板 |
| 语义记忆 | 可选模型无关嵌入、权威存档核验、可重建本地向量索引、词法/向量混合召回、结构化诊断与游戏时间重排 |
| 运行位置 | `netstandard2.1` 共享运行时可放在 Godot、Unity 或其他 C# 宿主；可选 .NET 8 HTTP/SSE 服务端与引擎客户端 |
| 引擎 | Godot 4.7 .NET 与 Unity 6 包，均已在 Windows 真实编辑器中通过测试 |

运行输入、模型内容、工具目录、循环、队列、进度事件与并发都有明确上限。每次模型调用前都会执行上下文准入，模型与工具调用都有截止时间，大型工具结果可以保存为产物而不是反复占满提示词。游戏可以替换内置的内存或本地文件实现。

### 无需手工拼接每个模型提供方

`OpenGameAgent.Models.BuiltIn` 会把内置模型目录变成可直接执行的运行时。目前它通过 9 种线路协议分发 27 个提供方定义与数百个可执行文本/工具模型，并统一应用提供方请求格式、推理参数、兼容性、成本、认证、取消与响应限界。开发者也可以绕过目录，直接使用底层 Provider 包连接一个明确的模型和端点。

`OpenGameAgent.Models.Auth.BuiltIn` 为支持的订阅服务提供可选浏览器或设备授权。框架不会内嵌公共客户端注册信息：需要 Client ID 的流程只有在游戏开发者显式提供后才会启用。`OpenGameAgent.ProviderTransport` 只向观察器暴露白名单内且有界的响应元数据，不会把凭证或任意响应头交给追踪代码。

图片、语音和视频生成使用独立的模型注册表，因为生成任务、渐进预览、轮询和输出并不是聊天补全。框架提供中立注册表、通用 HTTP 任务适配器和专用图片 Provider；本地生成器或其他 API 可以作为可选包注册，不需要修改 Agent 内核。

## 最小内核

```csharp
using OpenGameAgent.Kernel;
using OpenGameAgent.Providers.OpenAICompatible;

var provider = new OpenAICompatibleProvider(new(new HttpClient(), endpoint)
{
    ApiKey = Environment.GetEnvironmentVariable("MODEL_API_KEY")
});

var agent = new Agent(new AgentOptions(provider, "your-model")
{
    SystemPrompt = "你是游戏 NPC。需要改变世界时必须使用工具。"
});

using var events = agent.Subscribe((e, _) =>
{
    if (e.ModelEvent?.Delta is { } delta) Console.Write(delta);
    return default;
});

var result = await agent.RunAsync("你看到了什么？");
```

## 游戏 Runtime

```csharp
var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "your-model")
{
    Instructions = "只依据传入的游戏状态行动。改变世界必须调用工具。",
    ContextProvider = myGameContext,
    ToolProvider = myGameTools,
    SessionStore = mySessionStore
});

var input = new GameInput(
    sessionId: "save-42",
    actorId: "npc-blacksmith",
    type: "player_interaction",
    payloadJson: """{"intent":"repair","item":"sword","durability":0.35}""",
    moment: new GameMoment("main-world", tick: 18840),
    inputId: "interaction-9001");

var run = await runtime.RunAsync(input);
```

可继续阅读可编译的[互动世界示例](examples/OpenGameAgent.Example/Program.cs)和[入门指南](docs/getting-started.md)。

## Runtime 放在哪里

- **引擎内：** 单机最简单，可以直接读取游戏上下文，适合 BYOK 或本地模型 API。发布在客户端中的永久提供方 Key 可以被提取。
- **游戏服务端内：** 游戏本来就有权威服务端时最自然，让同一套 C# Runtime 靠近规则与存档。
- **独立 Agent 服务：** 适合官方承担推理费用、集中保管密钥、扩缩容或独立升级。引擎适配层通过 JSON/SSE 调用 `OpenGameAgent.Server`，并可经受认证的控制端点 steering 或 abort 活跃角色。

独立服务还提供受同一会话/Actor 所有者授权保护的持久 usage 查询，完整返回推理、缓存与分项费用。模型目录可为没有上报费用的 Provider 估算费用；没有价格数据时明确返回“未知”，不会伪装成零费用。

若客户端使用开发者付费的模型服务，应由开发者网关签发短期、有限作用域的凭证。永久上游 Key 留在开发者基础设施；框架提供客户端凭证流程，游戏负责登录、配额、吊销和滥用防护。

部署位置不会改变权威边界：只有游戏业务代码能够确认动作成功。

## 构建和验证

需要 .NET SDK 8.0。共享 Runtime 和服务端支持 Windows 与 Linux；引擎适配目前以 Windows 编辑器作为验证目标。

```powershell
dotnet restore OpenGameAgent.sln
dotnet build OpenGameAgent.sln -c Release --no-restore
dotnet test OpenGameAgent.sln -c Release --no-build --no-restore
./engines/godot/test-package.ps1 -GodotSharpDir <GodotSharp/Api/Debug>
./engines/godot/test-engine.ps1 -Godot <godot_console.exe> -GodotSharpDir <GodotSharp/Api/Debug>
./engines/unity/test-package.ps1 -UnityManagedDir <Unity/Editor/Data/Managed/UnityEngine>
./engines/unity/test-editor.ps1 -UnityEditor <Unity.exe> -UnityManagedDir <Unity/Editor/Data/Managed/UnityEngine>
```

真实编辑器门禁参见[引擎集成](docs/engine-integration.md)。

## 文档

- [入门指南](docs/getting-started.md)
- [架构与权威边界](docs/architecture.md)
- [功能与 API 地图](docs/features.md)
- [游戏集成模式](docs/game-integration-patterns.md)
- [引擎集成](docs/engine-integration.md)
- [部署与安全](docs/deployment-and-security.md)
- [生成式媒体](docs/media.md)

## 项目边界

这是面向开发者的框架，不是通用人物卡、战斗系统、世界包格式、可视化编辑器或 C 端游戏。具体玩法属于每一个游戏。框架提供构建对话角色、自主伙伴、社会模拟、AI 导演、动态任务与道具、策略 Agent、建造 Agent 和持续互动世界所需的 Agent Loop 与游戏原语。

## 署名

OpenGameAgent 使用 Apache-2.0 许可证，不强制游戏展示 Logo 或额外署名。我们欢迎游戏和 Mod 在 Credits、关于或第三方许可中写上：**“Powered by OpenGameAgent | opengameagent.com”**，或 **“本游戏使用 OpenGameAgent 构建游戏角色的 Agent 能力。”** 在许可证要求的情况下，请保留 [LICENSE](LICENSE) 和 [NOTICE](NOTICE)。Logo 与品牌使用方式见[品牌资源说明](docs/brand/README.md)。

## 协议

[Apache License 2.0](LICENSE)。可以基于框架制作闭源商业游戏或托管产品。参见 [CONTRIBUTING.md](CONTRIBUTING.md) 与 [SECURITY.md](SECURITY.md)。
