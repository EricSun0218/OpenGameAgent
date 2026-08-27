# 工具权限与策略

`@opengameagent/policy` 会在两个不同边界组合宿主派生的权限 scope 与逐工具策略：

1. `visibility` 在每次模型请求前过滤工具定义。
2. `execution` 在进入工具执行器前再次解析并校验权限。

因此，过期的工具目录或模型构造的 payload 都不能授予权限。scope provider 接收权威 `GameInput`；模型不能自行选择 scope ID 或 allowlist。

```ts
const policy = createGameToolPolicyResources({
  scopeProvider: {
    resolve: async (input) => await hostAuthority.resolveNpcScope(input.session),
  },
  policies: [worldRulePolicy, playerSettingsPolicy],
  audit,
});

const runtime = new GameAgentRuntime({
  // ...
  toolVisibility: policy.visibility,
  toolExecutionMiddleware: [policy.execution, approvals.execution],
});
```

scope 可提供精确的 `allowedTools` 与 `deniedTools`。工具收集阶段的策略能看到 input 和稳定工具定义；执行阶段还会看到已校验的 call，以及 runtime 生成的 run/turn/tool index。第一个非 allow 决定生效；隐藏或拒绝的工具永远不会进入执行器。

策略原因只保留在宿主审计中。模型只会看到稳定的 `tool_denied` 错误码，不会获知内部规则名称；审计契约也刻意不记录工具参数与结果。

策略和批准是两种不同控制。策略判断当前权限下能否展示或执行工具；批准则可让本来允许的高风险调用暂停，等待宿主一次性决定。
