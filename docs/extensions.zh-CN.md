# 开发 OpenGameAgent 扩展

OpenGameAgent 保持有状态的模型/工具循环精简，把可选能力放到扩展层。官方扩展与第三方扩展共同使用 `IGameAgentExtension` 和 `GameAgentExtensionApi`。

扩展可以提供上下文、工具、按输入决定的工具可见性、Skill、路由规则、待处理工作、工作流、Hook、提示片段、模型 Provider、类型化服务、生命周期处理器和类型化扩展间通道。每项注册都保留所有者、优先级、顺序和可释放生命周期。带命名空间的扩展状态随游戏会话持久化；只有扩展明确提供给模型时，模型才能看到它。

## 创建项目

在仓库 checkout 中运行：

```powershell
./tools/New-GameAgentExtension.ps1 `
  -Id my-studio.world-observation `
  -OutputDirectory ../MyWorldObservation

dotnet build ../MyWorldObservation/Extension.csproj -c Release
```

脚手架以 `netstandard2.1` 为目标，引用本地 `OpenGameAgent.Extensions` 项目，并创建 `extension.json` 开发清单。它不会动态安装或加载可执行代码。

## 开发清单

清单属于宿主与开发工具的契约，不会作为模型输入：

```json
{
  "schemaVersion": "1",
  "id": "my-studio.world-observation",
  "version": "1.0.0",
  "permissions": ["context.contribute", "tools.register"],
  "dependencies": [
    { "id": "my-studio.shared", "minimumVersion": "1.2.0" }
  ]
}
```

`GameExtensionDevelopmentManifest.Parse` 会严格、有界地检查 JSON、身份、语义化版本、权限与依赖。`GameExtensionPermissions` 给出已知权限及其对应资源类型。扩展必须声明实际注册的每一类资源；宿主还可以只允许其中一部分。

## 一致性冒烟测试

`GameExtensionConformance.RunAsync` 检查描述符与清单身份、依赖版本、宿主权限、实际注册资源、配置诊断、生命周期故障、超时与释放，并用有界假 Provider 发起一次真实 `GameAgentRuntime` 请求：

```csharp
var manifest = GameExtensionDevelopmentManifest.Parse(
    await File.ReadAllTextAsync("extension.json"));

var report = await GameExtensionConformance.RunAsync(
    new WorldObservationExtension(),
    manifest,
    new GameExtensionConformanceOptions
    {
        AllowedPermissions = new[]
        {
            GameExtensionPermissions.ContextContribute,
            GameExtensionPermissions.ToolsRegister,
        },
        AvailableExtensions = installedDescriptors,
        Timeout = TimeSpan.FromSeconds(10),
    },
    cancellationToken);
```

请把它加入扩展测试和打包门禁。假模型不会调用工具；游戏自有动作适配器仍须测试成功、拒绝、不确定结果、重复请求、重启和 revision 冲突。

### 动态 Skill 预算

Skill Provider 在每次收集时同时收到剩余的 `maximumSkills` 与 `maximumCharacters`。Provider 必须在这两个上限内返回资源；宿主还会用 `GameSkill.CharacterCount` 再次校验，超限时拒绝本次扩展输出。自定义 `IGameSkillSource` 应同时遵守 `GameSkillQuery.MaximumResults` 与 `MaximumCharacters`，避免先构造或序列化无法进入模型请求的内容。

## 依赖、重载与打包边界

- 清单依赖表达所需扩展版本；运行时协作应使用命名类型化服务或通道，不使用隐藏全局状态。
- Skill 等数据资源可以由数据源刷新。C# 扩展是宿主编译代码：仅在编辑器停止游戏、释放旧 `GameAgentRuntime` 后，加载新的宿主 generation 并重建运行时；活动 run 中不得替换可执行扩展。
- 可执行 C# 扩展使用正常的项目或包引用；可移植 Skill 与 MCP 配置使用 Agent Plugin 包。
- 引擎 SDK 类型与业务规则留在游戏适配器；凭据留在宿主认证或 Provider 配置，不得进入清单、提示、trace 或扩展状态。
