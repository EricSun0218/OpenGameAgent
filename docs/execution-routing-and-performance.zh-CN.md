# 执行路由与性能

OpenGameAgent 将低延迟回答、有界工具循环与持久编排分开，但不会建立互不兼容的多套 Agent Runtime。所有路径都使用同一个 `GameInput`、会话、上下文、Provider 边界、用量账本、追踪和游戏权威边界。

[English](execution-routing-and-performance.md)

## 执行模式

`AutomaticGameRoutePolicy` 会在产生用户可见的最终回答前确定路线：

| 输入 metadata | 含义 |
| --- | --- |
| `agent.route=auto` 或不填写 | 依次使用类型路由、结构化证据、可选分类器和保守回退。 |
| `agent.route=quick` | 只进行一轮模型请求，不向模型暴露或执行工具。 |
| `agent.route=agent` 或 `direct` | 执行可以调用零到多个工具的短任务 Agent loop；`direct` 会隐藏官方持久 Goal/TaskPlan 工具及相关指导。 |
| `agent.route=plan` | 使用普通 Agent loop，但显式注入持久计划指导，并开放任务清单工具。 |
| `agent.route=workflow:<name>` | 执行已注册的确定性或混合 Workflow。 |

解析顺序为：显式 metadata、输入类型规则、权威待处理工作、可选分类器、已有工具、最后才是 `QuickResponse`。待处理工作已经确定需要完整循环，因此不会额外调用路由模型；但“存在可用工具”并不等于当前输入需要工具，配置分类器后，普通对话即使处于完整工具环境中也可选择无副作用的 Quick 路线。未配置分类器时，只要存在工具就保守选择 Agent。`ModelGameRouteClassifier` 的耗时和 usage 会记为 routing，并与压缩、Workflow 输出和最终回答共享同一输入的模型 token 预算。

`ModelGameRouteClassifier` 接受 `JsonContent`、纯文本 JSON，或外部没有其他内容的单个 `json`/无语言 Markdown 围栏。它会拒绝外围说明文字、多个围栏、重复键、未知字段、错误字段类型、未知路线以及未注册 Workflow。`ModelGameRouteClassifierOptions` 会独立限制路由的输出 token、总 token 和 Provider 超时，默认分别为 128、2,048 和 5 秒。分类请求还会默认使用 `ReasoningLevel="off"`，避免推理模型在产生可见 JSON 前耗尽很小的路由预算。只有模型无法关闭推理时，才应把 `ReasoningLevel` 配成该 Provider 支持的级别；分类器仍只解析可见的 `JsonContent`/`TextContent`，绝不把 `ReasoningContent` 当路线决策。即使存在工具，有效的 `quick` 决策仍会生效。Provider 失败、超时、空输出、仅推理输出、无效 JSON、无效路线、预算耗尽或自定义分类器未返回决策时，分类阶段都不会获得工具权限；自动策略在有工具时保守回退 Agent、无工具时回退 Quick，并保留原始失败类别与回退原因，不再用笼统的 `tools-available` 掩盖。

Quick 是非试运行模式。它不能调用工具，不能通过工具写长期记忆、创建目标/计划或修改世界。`auto` 应在 Quick 输出最终回答之前完成分类，而不是先运行 Quick、再在可能出现副作用后重放输入。结构规则不足时，应配置类型路由或 `ModelGameRouteClassifier`。调用方显式强制 `quick` 等于做出“本次不需要能力升级”的承诺，框架不会偷偷改路。

Agent 路线原生承担短任务：多轮工具、进度、重试/回退、steer、follow-up、取消、上下文刷新和 durable action receipt 都不要求先创建计划。当任务扩大时，同一个 loop 可以调用官方 Goal/TaskPlan 工具创建持久工作；已经完成的世界写入保留原 operation ID 和 receipt，不会因为创建计划而重复执行。`direct` 会同时隐藏这些官方持久化工具及其提示指导，`plan` 则显式提供计划指导。Workflow 仍是预先注册的业务协议，不允许模型凭空创造游戏规则。

## 能力审计

| 验收项 | 框架覆盖 |
| --- | --- |
| Quick / Agent / Workflow | 完整：`GameRouteKind`、`AutomaticGameRoutePolicy`、`IGameWorkflow`。 |
| 显式 auto / direct / plan | 完整：使用 `agent.route`；别名复用现有三种稳定路线。 |
| 安全升级 | `auto` 通过执行前分类完成；Agent 通过 `TaskPlanExtension` 升级为持久计划；有意禁止 Quick 试运行后重放。 |
| Quick 无副作用 | 完整：Runtime 提供空工具集合，内核一轮后停止。 |
| 短多工具任务 | 完整：Agent 支持进度、steer、follow-up、abort、预算、刷新、重试和回退。 |
| 持久复杂工作 | 完整：Goal、TaskPlan、等待、Workflow、checkpoint、邮箱、游戏时间调度和宿主证据校验。 |
| 统一 AI 服务 | 完整：Provider/模型路由、工具及其可见性/策略、记忆、上下文、Skills、知识、实时语音、媒体、usage、trace、replay 与 eval。 |
| 稳定可观察状态 | 完整：生命周期事件、运行结果、扩展变更事件/只读查询、receipt、usage 和 trace recording。 |
| 权威世界写入 | 框架边界完整：类型化工具、operation ID、冲突键、journal、receipt 和 reconcile；游戏规则仍归游戏。 |
| 被动 Agent | 完整：没有 `RunAsync` 输入就不会路由、请求模型、写记忆或推进计划；跟随、寻路、动画等确定性维护属于游戏。 |

## 单次输入用量

`GameAgentRunResult.RunUsage` 只包含当前输入引起的用量；持久会话累计账本仍通过 `ReadUsageAsync` 读取。Cause 会区分 routing、compaction、assistant、工具相关模型工作、workflow 和 recovery。未知价格保持 unknown，不会被含混地记为 0。

## 耗时与可靠性指标

注册 `GameAgentTracingExtension`、读取有界 recording，然后生成机器可读摘要：

```csharp
var recording = await GameAgentTraceRecordingReader.ReadAsync("traces/run.jsonl", cancellationToken: token);
var metrics = GameAgentPerformanceSummary.Create(recording);

await File.WriteAllTextAsync("artifacts/metrics.json", metrics.ToJson(), token);
await File.WriteAllTextAsync("artifacts/metrics.jsonl", metrics.ToJsonLines(), token);
Console.WriteLine(metrics.ToText());
```

`GameAgentLatencyBreakdown` 会分别统计角色排队、输入准备、会话加载、上下文、工具收集、路由、Skills、端到端首响应、Provider 首响应、完整回答、首次工具、模型请求、工具执行、游戏宿主权威动作、durable action 框架处理、其他框架开销、执行总时长以及含排队总时长。

`route.selected` trace 现在包含 `classificationStatus`（`selected` 或 `fallback`）、`classificationFailure`（`provider`、`timeout`、`empty`、`reasoning-only`、`invalid-json`、`invalid-route`、`budget-exhausted` 或 `no-decision`）以及 `classificationFallbackReason`。它还只记录有界的响应形态：`classificationContentKinds`、`classificationVisibleContentCharacters` 和 `classificationReasoningCharacters`。HTTP Provider 失败时，`classificationProviderStatusCode` 与稳定的 `classificationProviderFailureCategory` 会提供 `invalid-request`、`authentication`、`rate-limit`、`server` 等安全传输诊断；`classificationProviderRequestFields` 只列出有界的顶层 JSON 字段名，`classificationProviderRequestId` 只接受来自白名单响应头且通过校验的标识符。Provider 响应正文、字段值、提示词、凭证和推理文本都不会被复制进路由 trace。`GameAgentRunPerformance` 会暴露同样字段和 `RouteReason`，`GameAgentPerformanceSummary` 会统计分类失败数与路由回退数。路由模型耗时继续与路由框架开销分开，路由 token/cost 仍归入 routing cause。

内置 DeepSeek Chat Completions 定义会在有界分类请求中使用该 Provider 的 `max_tokens` 字段，并在关闭分类器推理时发送 `thinking.type=disabled`。这很重要，因为普通 Agent 请求可能不设置最大输出字段，而有界分类器一定会发送。

对于已知的 Provider/模型组合，应通过 `BuiltInGameModelRuntime.CreateProvider(providerId)` 构造 Provider，并把同一个目录驱动 Provider 与模型 ID 同时交给分类器和主 Runtime。低层 `OpenAICompatibleProvider` 有意不根据 endpoint 或模型字符串猜测供应商；直接使用它时，调用方必须显式配置 `Protocol`。它的通用 OpenAI wire 现在会省略 Provider 中立的 `off`/`disabled`，不会再把它们错误序列化成 `reasoning_effort`；目录驱动 Provider 则会把这些值翻译为目标供应商真正支持的关闭机制。

工具结果可使用 `ToolFailureCategory`：`InvalidArguments`、`Authorization`、`RuleRejected`、`Transient`、`Timeout`、`Cancelled`、`Conflict`、`Internal` 或 `Unspecified`。自定义工具应返回自己能够证明的最精确类别。摘要按工具、失败类别、路线、实际 Provider 和模型聚合；durable 世界写入会单独计数，uncertain write 率只以这些写入为分母，同时统计 Provider 重试、任务清单重规划、回退、恢复和被拦截的重复写入。默认不会记录工具参数。对于官方通用 Goal/TaskPlan 工具，追踪只投影有界的 `action` 枚举，不会记录目标、步骤、证据或其他参数。

`DurableGameActionDispatcher.ExecuteDetailedAsync` 返回 `GameActionDispatchTimings`。`HostMilliseconds` 只衡量权威 handler 或 recovery；`FrameworkMilliseconds` 包括 operation 串行、journal、冲突等待、幂等检查和 receipt 持久化。这样不会把 OGA 的存储或协调时间错误归因给游戏逻辑。

Provider 重试/回退包装器只在真实发生后输出有界诊断。凭证、端点、任意响应头、完整 Prompt、工具参数和游戏 payload 不会被加入性能摘要。

## 实时语音与媒体

`RealtimeMetricsCollector` 是可选的有界观察器，统计 STT 首个/最终 transcript、TTS 首个/完整音频以及插话取消响应。将 `HandleAsync` 注册到 `RealtimeConversationManager.RegisterHandler`，并在请求取消前立即调用 `MarkBargeInRequested`。

`GameMediaMetricsCollector.GenerateAsync` 可以包装任意 `IGameMediaGenerator`，统计首次进度和资产可用或失败耗时。两者都不会修改传输、Provider、动作或持久化语义。

## Benchmark 与评测 Harness

`GameAgentBenchmarkRunner` 支持 warmup、并发、单轮超时和可配置阈值。Scenario 返回 trace recording，因此游戏可以组合固定或 fake Provider、确定性工具、故障注入、并发条件或真实 allowlisted Provider，而不修改 harness。

```csharp
var scenario = new GameAgentBenchmarkScenario("fixed-provider", async (iteration, token) =>
{
    return await RunScenarioAsync(iteration, token);
});

var report = await GameAgentBenchmarkRunner.RunAsync(
    new[] { scenario },
    new GameAgentBenchmarkOptions
    {
        Iterations = 50,
        WarmupIterations = 3,
        MaximumConcurrency = 8,
        IterationTimeout = TimeSpan.FromSeconds(30),
    },
    new GameAgentBenchmarkThresholds
    {
        MaximumFailureRate = 0.01,
        MaximumP95TimeToFirstResponseMilliseconds = 2_000,
        MinimumToolSuccessRate = 0.99,
        MaximumUncertainWrites = 0,
    },
    token);
```

报告可导出 JSON、JSONL 和人类可读文本。阈值由具体游戏或 CI 环境决定，框架不硬编码某个 Provider 或网络 SLA。Harness 只做观察，不会为了数字绕过工具策略、action receipt、幂等或 reconcile。

## 边界

- 路由只决定使用哪种执行原语，不负责战斗、背包、寻路、送礼、建造、UI 或权限规则。
- 游戏决定何时提交输入；空闲 NPC 的模型调用数为零。
- 游戏校验并提交每次世界修改；框架 trace 是编排证据，不是权威世界状态。
- 显式 `quick` 或 `direct` 会主动限制能力。希望框架根据工具、待处理工作、类型规则或分类器保守升级时使用 `auto`。
