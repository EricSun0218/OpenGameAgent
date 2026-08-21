# High-risk tool approval

`ToolApprovalExtension` is an optional, provider-neutral execution gate for calls that need host
consent. It complements rather than replaces schema validation, tool visibility, `ToolPolicyExtension`,
game-rule validation, and durable action receipts.

The modes are:

- `Disabled`: the tool never executes.
- `ExplicitOnly`: executes only when `IGameToolInvocationScopeProvider` attests that the host or
  player explicitly requested that exact tool.
- `AllowedInTask`: executes only inside a host-attested task that allowlists that tool.
- `ConfirmOnce`: creates one durable approval request and waits for the host broker.

Do not derive invocation scope or world revision from model output. The game supplies
`IGameToolInvocationScopeProvider` and `IGameToolApprovalWorldStateProvider` from authoritative
state. The final gate sees the fully rewritten and revalidated arguments. A confirmation binds the
session, actor, input, run, turn, tool call, tool name, canonical argument digest, game moment, save
generation, world revision, and optional task ID. Approval credentials are random and one-time; only
their hash is persisted, and neither hash nor credential is sent to the model, transcript, trace, or
remote client.

```csharp
var approvalStore = new FileGameToolApprovalStore(saveDirectory);
var approvalBroker = new GameToolApprovalBroker(approvalStore);

var runtime = new GameAgentBuilder(provider, model)
    .UseExtension(new ToolPolicyExtension(gamePolicies))
    .UseExtension(new ToolApprovalExtension(
        new[]
        {
            new GameToolApprovalRule(
                "confirm-shared-writes",
                GameToolApprovalMode.ConfirmOnce,
                minimumRisk: ToolRisk.NonIdempotentWrite),
        },
        approvalBroker,
        authoritativeWorldStateProvider,
        hostInvocationScopeProvider))
    .Build();
```

The in-process host calls `ListPendingAsync` and `RespondAsync`. A sidecar/native engine client uses
`POST /v1/approvals/pending` and `POST /v1/approvals/respond`, or
`ServerGameAgentClient.ListPendingToolApprovalsAsync` and `RespondToToolApprovalAsync`. Both server
operations pass through the same identity-derived session/actor authorizer as runs and durable
actions. The host renders its own UI; the framework provides no game-specific prompt or screen.

Pending, approved, denied, timed-out, cancelled, consumed, and expired states are revisioned and
persisted. A process restart preserves pending/audit state but cannot resurrect an interrupted agent
run or its plaintext credential. An orphaned request may be denied or allowed to expire; approving it
fails closed. Start a new run to create a newly bound request.

`GameAgentTracingExtension` records `tool.approval.pending` and `tool.approval.completed` without
arguments or credentials. DevTools reports `ApprovalWaitMilliseconds` separately from provider TTFT,
tool execution, host action latency, and framework overhead.
