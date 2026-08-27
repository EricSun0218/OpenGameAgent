# 结构化玩家交互

`@opengameagent/interactions` 是一个可选工具 Provider，用于需要玩家决策的 AI 角色。它不会把 UI 写进 Agent 循环：模型生成有界的问题和选项，游戏拥有的 `GameInteractionBroker` 在 Unity、Godot、Unreal、网页 UI 或无界面客户端中展示，并返回玩家回答。

每个问题支持 2～8 个选项、可选自由输入、可选多选，以及最多一个带说明的推荐选项。一次调用最多可合并 8 个相关问题。请求携带不可变的 session/input/run/turn/tool-call 坐标；完全相同的重放会得到稳定的请求 ID。

```ts
import { createStructuredGameInteractionToolProvider } from "@opengameagent/interactions";

const interactions = createStructuredGameInteractionToolProvider({
  broker: {
    async prompt(request, signal) {
      return await gameUi.askPlayer(request, signal);
    },
  },
});

const runtime = new GameAgentRuntime({
  // ...
  toolProviders: [interactions],
});
```

该工具按顺序执行并标记为中风险，因此宿主还可通过普通工具策略或审批中间件进一步约束。模型参数和 Broker 返回都会被严格校验。Broker 必须回答全部问题，或者整体取消；未知选项、重复回答、不合法自由文本、多个推荐项和超限请求都会失败关闭。

这个包只提供与 UI 无关的契约，不提供具体游戏界面。工具是否可见，以及问题、推荐回复、超时、无障碍和手柄交互如何呈现，都由游戏决定。
