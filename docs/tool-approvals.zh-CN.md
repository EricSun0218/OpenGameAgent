# 高风险工具批准

`ToolApprovalExtension` 是可选、与 Provider 无关的最终执行门禁，用于必须由玩家或运营方
同意的工具调用。它不会替代 schema 校验、工具可见性、`ToolPolicyExtension`、游戏规则校验
或持久动作 receipt。

四种模式分别是：`Disabled`（禁用）、`ExplicitOnly`（仅宿主证明的显式请求）、
`AllowedInTask`（仅宿主证明且允许该工具的任务）和 `ConfirmOnce`（创建一次持久批准请求）。

调用范围和世界版本必须来自游戏权威状态，不能来自模型输出。最终门禁发生在参数准备、
策略改写、schema 校验和冲突键计算之后。批准绑定 session、actor、input、run、turn、
tool call、工具名、规范化参数摘要、游戏时间、存档 generation、世界 revision 与可选任务 ID；
参数变化、读档或世界 revision 变化都会使批准失效。随机批准凭证只能使用一次，磁盘只保存
哈希；凭证和哈希都不会进入模型、会话、trace 或远程响应。

进程内宿主使用 `IGameToolApprovalBroker.ListPendingAsync` / `RespondAsync`。原生引擎或
sidecar 使用 `/v1/approvals/pending`、`/v1/approvals/respond`，也可通过
`ServerGameAgentClient` 的对应方法调用。服务器会先执行与 run、持久动作相同的
session/actor 所有者授权，再访问 broker。框架只提供类型化请求与响应，不提供具体游戏 UI。

Pending、Approved、Denied、TimedOut、Cancelled、Consumed、Expired 都会按 revision
持久化并可审计。重启后可以读取未决请求和审计，但不会复活已经中断的 Agent run 或明文
一次性凭证；孤儿请求只能拒绝或等待过期，批准会安全失败。新 run 会生成重新绑定的请求。

`GameAgentTracingExtension` 只记录无参数、无凭证的批准生命周期。DevTools 将
`ApprovalWaitMilliseconds` 与 Provider TTFT、工具执行、宿主动作和框架开销分别统计。
