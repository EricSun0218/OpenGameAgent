# Agent 循环与性能

[English](agent-loop-and-performance.md)

OpenGameAgent 用同一种执行模型处理对话、工具调用和复杂任务：

1. 收集有界的游戏上下文、Skills 与当前已授权工具。
2. 发起一次模型请求。
3. 模型返回 Assistant 消息时，本轮结束。
4. 模型返回工具调用时，框架先校验并执行工具，写入结构化结果，刷新上下文和动态工具，再继续同一循环。

框架不会预先调用“任务复杂度分类模型”，也没有彼此独立的 Quick、Agent、Plan 或 Workflow Runtime。问候可以只请求一次模型就结束；建造、调查、谈判或战斗辅助任务则可以按需要运行一个或多个工具回合，而不用切换执行引擎。

## 可选的持久规划

持久 Goal 与有序 TaskPlan 是扩展工具，不是另一条路线。只有 NPC 的工作需要跨后续输入保存时，才安装 `GoalLoopExtension` 或 `TaskPlanExtension`。同一个循环既可以直接回答、使用普通工具，也可以创建持久计划。

宿主决定某次输入能否看到这些工具：

```csharp
var runtime = new GameAgentBuilder(provider, model)
    .Configure(options => options.ExecutionScopeProvider = (input, cancellationToken) =>
        new ValueTask<GameExecutionScope>(
            hostPolicy.AllowsPersistentPlanning(input.SessionId, input.ActorId)
                ? GameExecutionScope.Unrestricted
                : GameExecutionScope.NoOptionalCapabilities))
    .UseExtension(new GoalLoopExtension())
    .UseExtension(new TaskPlanExtension(hostEvidenceValidator))
    .Build();
```

Scope 必须来自经过认证的宿主策略，不能把客户端 metadata 的自报字段直接变成能力授权。未授权时，持久规划上下文和工具会在模型请求前被隐藏，普通游戏工具仍然可用；已有计划继续安全保存在存储中，要等后续输入重新获得能力后才能修改。

过月演化、战斗结算、经济结算、资产导入或任务状态迁移等固定游戏流程，仍然属于游戏自己的状态机和工具，不需要第二套模型循环。

## 延迟归因

`GameAgentTracingExtension` 记录有界耗时，不复制输入正文、工具参数、凭证或隐藏推理。`GameAgentPerformanceSummary.Create(recording)` 可以区分：

- 角色队列与会话加载；
- 输入准备、具名上下文 Provider、工具收集与 Skill 选择；
- 每次模型请求、Provider 首包与完整回复；
- 首次工具调用、各工具耗时、等待批准、游戏宿主权威动作耗时和 durable action 框架耗时；
- 重试、Provider 回退、精确工具重复保护、未知写入、恢复、Token 和已知/未知成本。

由于没有分类模型请求，首字耗时和用量都属于真正的 Agent 工作。直接回答只需要一次 Provider 请求；工具只增加读取其结果所必需的后续模型轮次。

## 性能规则

- 限制上下文和工具 Schema；大型工具结果应存为 Artifact。
- 每次模型请求前过滤工具可见性，让模型只看到真正可用的能力。
- 只有工具回合确实改变能力目录时，才依赖动态工具刷新。
- 不能为了降低延迟而跳过可靠动作日志、回执、冲突协调或批准门禁。
- Benchmark 应分别衡量框架、Provider 和游戏宿主耗时。

Benchmark Harness 支持 Fake 或固定 Provider、确定性工具、故障注入、并发、JSON/JSONL 导出与可配置阈值。详见[开发工具](devtools.zh-CN.md)。
