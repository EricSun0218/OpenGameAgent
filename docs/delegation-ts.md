# Delegated agents and background tasks

`@opengameagent/delegation` is an optional extension for bounded, isolated work that can continue in the background. It reuses the host's `GameAgentRuntime`; it does not create a second model/tool loop or grant new game authority.

Use delegation when an NPC should ask a registered specialist to inspect, research, plan, or perform another independently bounded task. Use an ordinary tool call for a single game action, and use durable goals or task plans when the same actor owns a long-lived game objective.

## Safety and persistence model

- The host registers the delegates available for each `GameInput`. The model cannot invent a delegate.
- Records are isolated by the complete `GameSessionKey`, including timeline and generation.
- A stable delegation id makes an exact tool-call replay idempotent.
- SQLite stores request lineage, attempts, status, bounded results, and renewable leases.
- Expired leases may be reclaimed with a larger fencing token. A stale worker can finish inference, but the runtime checks current authority again immediately before every tool execution.
- Cancellation is durable. Process shutdown leaves uncooperative work recoverable instead of claiming that it completed.
- Parent context is not inherited by default. The host must enable it per delegate and provide an explicit bounded projection through `captureContext`.
- Model-facing status omits canonical session coordinates, parent run/input ids, lease secrets, and fencing tokens.

The built-in SQLite store coordinates processes sharing one database file. A multi-host service should implement `GameDelegationStore` over transactional shared storage with equivalent lease and fencing semantics.

## Minimal setup

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
    if (!runtime) throw new Error("Runtime is not composed.");
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
      ? [{ id: "scout", description: "Inspect a bounded area.", maximumTurns: 6 }]
      : [],
});

runtime = new GameAgentRuntime({
  kernel,
  baseSystemPrompt: "Operate only through registered game tools.",
  defaultModelProfileId: "default",
  toolProviders: [delegation.toolProvider],
  postToolContextProviders: delegation.postToolContextProvider
    ? [delegation.postToolContextProvider]
    : [],
});

await manager.resumePending();
```

`getRuntime` breaks the construction-time dependency cycle without making runtime composition mutable. It must resolve before the first delegated task starts. The stable integration units are the returned `toolProvider`, optional `postToolContextProvider`, `GameDelegationManager`, and `GameDelegationStore`.

Dispose the manager during shutdown. It aborts active local runs, stops lease renewal, waits for a bounded drain period, and leaves any unconfirmed work available for reclaim after its lease expires.

## Model tools

The extension supplies five tools when allowed by the input:

- `delegate_agent_task`
- `read_delegated_task`
- `list_delegated_tasks`
- `steer_delegated_task`
- `cancel_delegated_task`

Recursive delegation disappears at the configured depth limit. Reads, steering, and cancellation always use the current input's complete session key.

## Context inheritance

Context inheritance requires both delegate authorization and a host projection:

```ts
const delegation = createGameDelegationExtension({
  manager,
  delegates: () => [{
    id: "scout",
    description: "Inspect a bounded area.",
    allowContextInheritance: true,
  }],
  captureContext: (input) => ({
    visibleRegion: input.context?.["visibleRegion"] ?? null,
  }),
});
```

Do not copy an unbounded transcript or private game state into a child. Project only the data that the selected delegate is allowed to see.
