# 委派 Agent 与后台任务

`@opengameagent/delegation` 是可选扩展，用于执行有界、隔离、可在后台继续的任务。它复用宿主已有的 `GameAgentRuntime`，不会建立第二套模型/工具循环，也不会扩大游戏权限。

当 NPC 需要让已注册的专长角色去调查、研究、规划或完成其他独立有界任务时，可以使用委派。单次游戏动作仍应直接调用普通工具；由同一角色长期负责的游戏目标，应使用持久 Goal 或 TaskPlan。

## 安全与持久化语义

- 宿主按 `GameInput` 注册本次可用的委派角色，模型不能凭空创建委派角色。
- 记录使用完整 `GameSessionKey` 隔离，其中包括时间线和存档世代。
- 稳定委派 ID 让完全相同的工具调用重放保持幂等。
- SQLite 持久化谱系、尝试次数、状态、有界结果和可续期租约。
- 租约过期后可以用更高 fencing token 回收。旧 Worker 即使完成推理，也必须在每次工具执行前重新通过当前权威校验。
- 取消会持久化。进程退出时不会把未确认完成的任务误记为成功或取消，而是保留为可恢复状态。
- 默认不继承父级上下文。宿主必须同时为具体委派角色开启权限，并通过 `captureContext` 提供明确、有界的数据投影。
- 模型可见状态不会包含权威 session 坐标、父 run/input ID、租约密钥或 fencing token。

内置 SQLite Store 适合同一数据库文件上的本机多进程协调。多机服务应在事务型共享存储上实现 `GameDelegationStore`，并保持相同的租约与 fencing 语义。

## 最小接入

```ts
import {
  GameDelegationManager,
  RuntimeGameDelegationExecutor,
  SqliteGameDelegationStore,
  createGameDelegationExtension,
} from "@opengameagent/delegation";

const store = new SqliteGameDelegationStore("./save/delegations.sqlite");
let runtime: GameAgentRuntime | undefined;
const executor = new RuntimeGameDelegationExecutor({
  getRuntime: () => {
    if (!runtime) throw new Error("Runtime 尚未完成装配。");
    return runtime;
  },
  createInput: (request) => ({
    id: `child-${request.id}`,
    type: "agent.delegation",
    session: request.session,
    moment: request.parentMoment,
    content: [
      { type: "json", value: request.task },
      ...(request.inheritedContext === undefined
        ? []
        : [{ type: "json" as const, value: request.inheritedContext }]),
    ],
  }),
});

const manager = new GameDelegationManager({
  store,
  executor,
  maximumConcurrent: 4,
});

const delegation = createGameDelegationExtension({
  manager,
  delegates: (input) =>
    input.type === "npc.chat"
      ? [{ id: "scout", description: "调查一个有界区域。", maximumTurns: 6 }]
      : [],
});

runtime = new GameAgentRuntime({
  kernel,
  baseSystemPrompt: "只能通过已注册的游戏工具行动。",
  defaultModelProfileId: "default",
  toolProviders: [delegation.toolProvider],
  postToolContextProviders: delegation.postToolContextProvider
    ? [delegation.postToolContextProvider]
    : [],
});

await manager.resumePending();
```

`getRuntime` 用于解除构造阶段的循环依赖，不会把 Runtime 变成可变注册表；它必须在第一个委派任务启动前返回已经完成装配的 Runtime。稳定接入单元是返回的 `toolProvider`、可选 `postToolContextProvider`、`GameDelegationManager` 和 `GameDelegationStore`。

宿主退出时应释放 Manager。它会中止本进程正在运行的任务、停止租约续期、在有界时间内等待收尾；未确认结果的任务会在租约到期后重新进入可恢复队列。

## 模型工具

扩展会按输入提供五个工具：

- `delegate_agent_task`
- `read_delegated_task`
- `list_delegated_tasks`
- `steer_delegated_task`
- `cancel_delegated_task`

到达配置的递归深度后，创建新委派的工具会直接隐藏。读取、调整和取消始终绑定当前输入的完整 session key。

## 上下文继承

上下文继承必须同时具备委派角色授权与宿主投影：

```ts
const delegation = createGameDelegationExtension({
  manager,
  delegates: () => [{
    id: "scout",
    description: "调查一个有界区域。",
    allowContextInheritance: true,
  }],
  captureContext: (input) => ({
    visibleRegion: input.context?.["visibleRegion"] ?? null,
  }),
});
```

不要把无界会话记录或私密游戏状态整体复制给子任务，只投影该委派角色确实有权看到的数据。
