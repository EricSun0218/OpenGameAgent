# Tool authority and policy

`@opengameagent/policy` composes host-derived authority scopes with per-tool policy at two separate boundaries:

1. `visibility` filters tool definitions before every model request.
2. `execution` resolves authority again immediately before a tool executor runs.

This prevents a stale catalog or a model-authored payload from granting authority. The scope provider receives the canonical `GameInput`; the model cannot choose a scope ID or allowlist.

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

A scope may contain exact `allowedTools` and `deniedTools`. Policies see the input and stable tool definition during advertisement, plus the validated call and trusted run/turn/tool index during execution. The first non-allow decision wins. A hidden or denied tool never reaches its executor.

Policy reasons remain in the host audit record. Model-visible denial results contain only the stable `tool_denied` code, so internal rule names are not disclosed. The audit contract intentionally excludes tool arguments and results.

Approval and policy are different controls. Policy answers whether a tool may be advertised or executed under current authority. Approval may additionally pause an otherwise permitted high-risk invocation for a host decision.
