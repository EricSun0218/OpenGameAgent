# Provider 一致性测试

`GameProviderConformance` 是面向 `IModelProvider` 适配器的有界、中立一致性 runner，位于 `OpenGameAgent.Models`，不进入 Kernel，也不属于任何厂商包。适配器作者可以给 Provider 注入脚本化 HTTP/WebSocket 传输，使原生、兼容、远程和本地 Provider 接受同一套规范化流校验。

```csharp
var report = await GameProviderConformance.RunAsync(
    providerBackedByScriptedTransport,
    GameProviderConformanceFixtures.CreateToolRequest("fixture-model"),
    new GameProviderConformanceOptions
    {
        RequireProviderIdentity = true,
        ForbiddenValues = new[] { testCredential },
        Timeout = TimeSpan.FromSeconds(10),
    });
```

Runner 会检查：流消费前 preflight、唯一且位于首位的 `Started`、事件数量与耗时限界、文本/推理/工具内容生命周期、唯一终态、终态后不得继续发送事件、终止原因一致性、可选的实际 Provider/模型身份，以及错误和诊断中不得出现宿主指定的敏感值。`RunCancellationProbeAsync` 会用单独的阻塞 fixture 验证传输确实响应取消。

标准 fixture 提供有界的纯文本请求和带工具请求，不访问公网模型，也不包含凭证。每个厂商适配器仍应在自己的测试里喂入代表性的原始协议帧；一致性 runner 校验统一公开契约，不复制各家的 wire parser。

不透明推理连续性属于 Provider 适配器边界。带签名、加密、结构化或已遮蔽的推理状态，只能在生成它的同一 Provider/API/模型组合中保留，并必须使用该协议的原生字段重放，不能进入非内部投影。OpenAI-compatible 的结构化推理块与 Bedrock 的遮蔽推理都会在写入规范会话前接受大小限制和结构校验；切换 Provider、API 或模型时会移除不透明状态。

一致性测试不代表服务在线、回答质量、价格信息或具体游戏工具已经验证。除了各 Provider 包自身的协议测试，还应运行：

```powershell
dotnet test tests/OpenGameAgent.Models.Tests -c Release
```
