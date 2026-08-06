# OpenGameAgent

[English](README.md)

面向 AI 原生游戏、自治 NPC 与持续演化世界模拟的开源 C# Agent Runtime。既可嵌入
Godot 或 Unity，也可把同一套 Runtime 部署在 .NET 游戏服务中。

[![CI](https://github.com/EricSun0218/OpenGameAgent/actions/workflows/ci.yml/badge.svg)](https://github.com/EricSun0218/OpenGameAgent/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![Status](https://img.shields.io/badge/status-alpha-orange.svg)](CHANGELOG.md)

OpenGameAgent 接收类型化游戏上下文，运行流式模型/工具循环，通过游戏引擎派发
动作，并记录足以在崩溃后安全恢复的证据，避免盲目重复副作用。输入可以是文本、JSON、
数字、游戏事件或对游戏自有资源的引用，不要求使用自然语言。

> 当前版本：`0.2.0-alpha.1`。在 `1.0` 之前，公开 API 与线协议仍可能变更。

## 为什么游戏需要专用 Agent Runtime？

通用 Agent 往往默认单个用户、现实时间和近似线性的对话。游戏还需要命名时钟与时间线、
存档分叉、权威状态变更、受限的帧线程工作量、大量 NPC 并发、确定性的冲突处理、离线
模拟，以及绝不盲目重试副作用的恢复机制。OpenGameAgent 把这些问题做成可复用的
运行时能力，同时把业务规则与状态所有权留在游戏代码中。

它可用于对话角色、自治同伴、AI 游戏主持人、社会推理智能体、动态任务与生成内容、
持续演化的世界，或通过类型化工具控制传统游戏机制。

## 产品边界

本仓库是 Agent Runtime，不是游戏、世界编辑器、内容格式或面向最终玩家的宿主。游戏
负责状态、规则、权限、UI、存档格式和最终写入；Runtime 负责可复用的 Agent Loop，
以及安全、持久化、调度、记忆和模型提供方边界。

同一套 Runtime 因此可以驱动 NPC、导演系统、模拟工作进程、助手或群体决策，而不强迫
游戏采用某一种数据模型。

## 核心能力

- 可持久化的流式模型/工具循环，支持重试、路由回退、过期流隔离、崩溃恢复和不确定写入
  的显式对账。
- 无状态补全，以及可持久化的 `Direct`、完整 `Agent` 和固定 `Workflow` 执行路径；
  采用有界混合自动路由，明确的短对话保持快速，动作或结构化输入保留 Agent 能力，
  显式能力要求始终优先。
- 类型化观察和结构化工具结果；自然语言只是可选输入之一。
- 不可变工具与 Skill 快照，以及有界的渐进式披露。
- 严格的工具输入校验、确定性冲突域、并行只读、冲突写入串行化和引擎主线程派发。
- Turn、Token、时长、费用、动作数、队列和模型工作负载预算。
- 请求准备、上下文裁剪、可审计的派生压缩和持久化用量核算，不重写权威对话记录；
  对话上下文引擎可替换且有界。
- 可插拔记忆：本地 BM25、可选有界向量存储、RRF 混合融合、有界查询变换与重排，
  以及抗崩溃文件存储。
- 精确调用与参数抖动循环守卫：阻止重复工具工作，同时允许在确有进展后确定性恢复。
- 取消、中断、运行中引导和后续消息控制。
- 每次操作独立的推理、采样、提示缓存和有序模型路由控制；不支持的参数在传输前明确
  拒绝。
- 围绕运行、模型派发和工具批次的类型化必需/可选生命周期中间件，并隔离有界回调。
- 用于在 Agent 步骤外围进行确定性编排的可持久化 Workflow。
- 安全的模型生成命令计划：支持有序和并行 DAG 阶段、有界 foreach/reduce/反馈循环、
  持久化等待与宿主回执。
- 游戏专用坐标：命名时钟、时间线、观察视角、实体转世、状态版本、空间上下文和因果
  来源。
- 可持久化游戏时间触发器、稳定 Agent 身份与邮箱、有界驻留、会话上下文增量、带引用
  的记忆蒸馏、外部注意力，以及世界/群体/Agent 分层预算。
- 有界多角色批处理和可持久化群体交互，参与者故障相互隔离，结果顺序确定。
- 有界子 Agent 监管：持久化谱系、深度与并发限制、取消传播和故障隔离批次。
- 原生 OpenAI Responses、Gemini Interactions、Anthropic Messages，以及可配置的
  OpenAI-compatible 流式适配器；模型目录不可变，并进行显式能力协商。
- 与模型提供方无关的图片、视频、语音和结构化内容任务，可接本地或远程 API；支持
  持久化轮询/取消、安全制品导入、流式语音和宿主校验的内容事务；项目不内置模型。
- 共享 `netstandard2.1` 核心，以及 Godot 和 Unity 接入边界。
- Runtime 可运行在引擎内或服务端；支持带身份认证的 WebSocket 动作桥、
  SQLite/PostgreSQL 日志、租户准入和标准遥测。
- 面向大规模 NPC 的确定性生活世界细节层级调度，以及离线玩法评估。

## 架构

```text
游戏代码
  观察 -> Agent Runtime -> 动作请求
    ^                       |
    |                    游戏处理器
    +------ 权威回执 --------+

Agent Runtime
  上下文 + 记忆 + Skills + 工具
  -> 模型流
  -> 校验并调度工具调用
  -> 日志 + 检查点 + 指标
```

`ActionReceipt` 是权威边界。只有游戏能声明一次变更成功、被拒绝、失败或结果未知。
Runtime 不会虚构成功的游戏状态变更。

详细契约参见[架构](docs/architecture.md)、[协议](docs/protocol.md)和
[游戏语义](docs/game-semantics.md)。

## 引擎支持

| 目标 | 当前范围 |
| --- | --- |
| Godot 4.7 .NET | 首要集成路径。进程内 C# Runtime、Autoload 生命周期、类型化与 GDScript 桥、有界主线程/事件泵、多角色支持、打包，以及 Windows 桌面和无头验证。 |
| Unity 2022.3+ | 进程内 C# 宿主和 UPM 包，具备托管编译、打包、制品加载、生命周期与一致性门禁。Unity 6000.5.6f1 已在 Windows 通过有许可证的 EditMode、PlayMode、Mono Player 和 IL2CPP Player 构建运行门禁。 |

引擎 SDK 只是适配器。Agent 行为、持久化语义和模型提供方逻辑都留在共享 Runtime 中。

可复用 Agent 行为可以随游戏进程运行，也可以部署在 .NET 游戏服务中。只把模型端点
放到远端，仅会改变模型传输；托管 Runtime 还可以拥有 Workflow、记忆和日志。无论采用
哪种部署方式，游戏规则、存档和权威动作结算仍由游戏所有。

## 快速开始

在 Windows 或 Linux 检出仓库后执行：

```powershell
dotnet build GameAgentRuntime.sln -c Release
dotnet test GameAgentRuntime.sln -c Release --no-build
```

接下来阅读：

- [入门指南](docs/getting-started.md)
- [Godot 集成](engines/godot/README.md)
- [Unity 集成](engines/unity/README.md)
- [工具、Skills 与记忆](docs/tools-skills-memory.md)
- [执行与扩展参考](docs/execution-and-extension-reference.md)
- [路由工作与监管子 Agent](docs/how-to-route-and-supervise-agents.md)
- [游戏集成模式](docs/game-integration-patterns.md)
- [持续演化世界集成](docs/living-world-integration.md)
- [持续演化世界调度](docs/living-world-scheduling.md)
- [部署与远程托管](docs/deployment-and-remote-hosting.md)
- [评估与可观测性](docs/evaluation-and-observability.md)
- [原生模型提供方](docs/native-model-providers.md)
- [Runtime 能力模型](docs/runtime-capability-model.md)
- [多媒体与生成内容](docs/media-and-generated-content.md)
- [可持久化 Workflow](docs/durable-workflows.md)
- [群体交互](docs/group-interactions.md)

公开发布后，使用 [GitHub Discussions](https://github.com/EricSun0218/OpenGameAgent/discussions)
提问和寻求支持；Bug 与功能建议使用仓库 Issue 表单。疑似安全漏洞必须遵循
[安全策略](SECURITY.md)。

## 安全与部署

在低延迟和直接引擎集成更重要时，把 Agent Runtime 放在游戏进程中。不要在客户端构建
中携带能使用你的商业模型账户的长期密钥。BYOK 模式使用玩家自己的凭证；官方模型服务
应由你控制的服务把游戏身份兑换为短期、最小权限的访问能力。

工具和 Skills 是能力，不是提示词。权威校验和状态变更必须留在游戏代码中；只暴露最窄
的工具面，并在认为一次写入可以重试之前持久化操作回执。

## 发布验证

仓库包含确定性打包、隐私、版本一致性、托管消费者、Godot、Unity、性能和真实模型
提供方门禁。发布制品前必须遵循[公开发布前清单](docs/pre-public-release.md)。

## 许可证

本项目使用 [Apache License 2.0](LICENSE)。贡献前请阅读
[贡献指南](CONTRIBUTING.md)和[行为准则](CODE_OF_CONDUCT.md)。
