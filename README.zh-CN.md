# OpenGameAgent

[English](README.md) | [简体中文](README.zh-CN.md)

OpenGameAgent 是面向 AI 原生游戏角色与世界的 TypeScript 运行时，采用 MIT 许可证。
框架负责 Agent 循环、运行控制、持久化与工具调用；游戏负责规则、权限和权威世界状态。

当前为持续开发中的源码预览版。接入项目时请固定 Git 提交，不要将历史 C# 发布包与当前源码混用。

## 快速开始

准备 Node.js 24.15.0（见 `.nvmrc`）和 npm 11.12.1：

```sh
git clone https://github.com/EricSun0218/OpenGameAgent.git
cd OpenGameAgent
npm ci
npm run build
npm run example:local
```

[本地示例](examples/local-runtime.mjs) 会注册一个角色、提交游戏输入并打印回复。
示例使用确定性的模型替身与内存事件存储，不需要 API Key，也不会调用外部服务。
真实游戏接入请阅读[开发者集成说明](docs/developer-integration.md)，配置模型与凭据、SQLite 持久化、工具权限和游戏动作。

TypeScript 工作区目前以源码形式提供。`package.json` 中的 `private: true` 用来防止意外发布到 npm，
不限制 MIT 许可证授予的使用与分发权利。以前的 C# 版本仍保留在 Git 历史与 Releases 中。

## 核心能力

- 基于 Pi 的单一 Agent 循环，支持运行准入、取消、排队、延迟响应恢复与精确运行标识。
- SQLite 权威存储、事件回放、可靠 Actions、审批与结构化玩家交互。
- 角色设定与世界书、游戏时间记忆、混合检索，以及可选的本地或远程嵌入适配器。
- 目标、信号、日程、委派任务、行为 Skill、Agent Skills、Agent Plugins 和 MCP 工具接入。
- 图像附件与视觉观察、持久化媒体任务、语音识别与合成、实时语音协调。
- 服务端、TypeScript 客户端与 Unity、Godot、Unreal Engine 集成。

完整包结构与各包职责见[英文 README](README.md#architecture)。各模块按需组合，游戏始终保有世界状态写入权。

## 游戏引擎

| 引擎 | 接入方式 | 文档 |
| --- | --- | --- |
| Unity 6 | C# 包与侧车运行时 | [Unity](engines/unity/Packages/com.opengameagent.client/README.md) |
| Godot | C# 客户端与引擎节点 | [Godot](engines/godot/README.md) |
| Unreal Engine | C++ 插件与侧车运行时 | [Unreal](engines/unreal/README.md) |

## 开发与验证

```sh
npm run gate
```

该命令检查生成产物、格式、静态分析、类型、测试、构建、媒体验收、包内容、依赖漏洞及公共源码边界。
GitHub Actions 在 Ubuntu 24.04 上执行相同流程；Unity、Godot 与 Unreal 的真实编辑器验收需要另行配置本地环境。
命令与证据范围见[验证说明](docs/verification.md)。

提交改动前请阅读[贡献指南](CONTRIBUTING.md)与[行为准则](CODE_OF_CONDUCT.md)。
安全问题请按[安全政策](SECURITY.md)私密报告。

## 许可证

项目使用 [MIT 许可证](LICENSE)。第三方依赖保留各自的许可证，详见[第三方声明](THIRD_PARTY_NOTICES.md)。
各引擎集成目录和工作区包均包含项目许可证副本。
