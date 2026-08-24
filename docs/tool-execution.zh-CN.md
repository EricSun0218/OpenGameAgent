# 工具执行安全与并发

内核会在派发前完成工具调用准备：兼容参数整理、JSON Schema 校验、宿主策略改写、最终授权和冲突键解析。执行顺序与精确重复保护使用的是执行器最终收到的参数，而不是模型最初生成的未可信草稿。

## 有序执行分段

`AgentOptions.ToolExecution` 选择默认策略：

- `Sequential`：每个调用都是一个按顺序执行的单调用分段；
- `SafeParallel`：连续只读调用和显式标为 `Parallel` 的工具可以并发，未显式允许并发的写入是顺序屏障；
- `Parallel`：除非工具显式标为 `Sequential`，否则连续调用可以并发。

顺序工具是屏障，不会再导致整份模型响应全部串行。屏障之前的可并发调用先全部结束，屏障单独执行，屏障之后的调用再按条件并发。所有并发分段都受 `MaxConcurrentTools` 限制。完成事件保持真实完成顺序，写入规范会话的工具结果仍保持模型源码顺序。

冲突键和未知写入结果仍是强安全约束：同键调用在并发分段内串行；同键未知写入会阻断之后的同键写入；没有冲突键的未知写入会阻断本批次之后的所有写入。取消发生后，当前分段结束时不会再派发后续分段。

## 模型可见的持久动作回执

`GameActionTool` 会为动作 journal、宿主事件、恢复、冲突协调、重复执行拦截和指标保留一份完整 canonical 回执。为保持默认兼容，不配置时同一份回执 JSON 也会发给模型。如果宿主不希望模型上下文包含权威坐标或观测数据，可在创建工具时传入 `modelReceiptProjector`：

```csharp
var tool = GameActionTool.Create(
    input,
    "build",
    "Build a registered structure.",
    schemaJson,
    dispatcher,
    modelReceiptProjector: context =>
    {
        using var result = JsonDocument.Parse(context.Receipt.ResultJson);
        return JsonSerializer.Serialize(new
        {
            action = context.Intent.Action,
            status = context.Receipt.Status.ToString().ToLowerInvariant(),
            label = result.RootElement.GetProperty("label").GetString(),
            reward = result.RootElement.GetProperty("reward").Clone(),
        });
    });
```

这个选项只替换发给模型的 `JsonContent`。`ToolResult.DetailsJson` 与持久化的 `GameActionReceipt` 仍保持完整，因此宿主继续获得 operation identity、状态 revision、timeline、执行/重放/恢复状态和耗时。可信宿主的 projector 只接收稳定的 intent 与 receipt；只与本次派发有关、在重放时可能变化的 disposition 和 timing 不在输入中。同一组输入必须始终生成同一结果。

自定义投影必须是无重复字段、长度不超过 `GameActionTool.MaximumModelReceiptCharacters` 的 JSON 对象。projector 抛错，或返回 null、非法 JSON、非对象、超限 JSON 时，工具会返回固定且标记为终止的 `projection_failed`；该结果关闭当前批次时，不会再发起下一次模型请求。框架绝不会回退暴露 canonical 回执。此时权威动作可能已经提交，UI 错误处理应读取 canonical 回执，不要直接重试世界写入。

## 精确重复循环保护

内核会对工具名与深度规范化后的最终 JSON 参数生成不泄露参数的指纹，并在一次 Run 的多轮模型调用间追踪连续相同调用。默认策略为：

- 第 3 次重复发布带 `Advisory` 的 `AgentEventKind.ToolRepeatDetected`，并为下一次模型请求追加一条有界 `agent_policy` 提示；
- 第 8 次重复发布 `Terminated`，不派发该调用，返回错误工具结果并结束循环。

通过 `AgentLimits.ExactToolRepeatAdvisoryThreshold` 与
`AgentLimits.ExactToolRepeatTerminationThreshold` 调整阈值；设为 0 可关闭对应动作。真实的 steering 或 follow-up 会重置序列，因为模型已经获得新证据。

有些观察工具确实需要反复轮询相同状态，可在构造工具时设置 `trackExactRepeats: false`。被豁免的调用既不增加、也不重置其他受追踪序列；全局轮数、工具数、超时和 token 上限仍然生效。不要为了掩盖错误循环而豁免状态写入工具。

追踪中会记录不含参数的 `kernel.toolrepeatdetected`。`GameAgentPerformanceSummary` 提供每次运行及汇总的提示/终止计数。默认 audience 策略仍把包括重复策略事件在内的工具生命周期事件视为内部信息。
