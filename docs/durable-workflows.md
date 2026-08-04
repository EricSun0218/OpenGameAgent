# Durable workflows

`GameAgent.Workflow` is an optional composition plane for bounded, recoverable
jobs. The Agent Runtime core does not depend on it.
Games can use it for multi-stage authoring pipelines, background NPC jobs,
parallel independent decisions, reductions, or bounded iterative work without
making workflow order part of frame timing.

## Execution guarantees

- Definitions compile into a validated, declaration-order-independent DAG.
- Step, foreach, reduce, and bounded loop stages use closed JSON schemas.
- Run, stage, item, and iteration identities derive from canonical input.
- A stage is durably marked `Started` before its executor is called.
- Revision compare-and-swap, expiring leases, owner fencing, and generation
  checks prevent an old worker from committing after recovery.
- Outputs, checkpoints, cursors, usage, cancellation, and terminal status are
  persisted with explicit byte, item, attempt, duration, and parallelism caps.
- Fan-in and reduction order follow compiled definition order, never task
  completion order.
- Numeric schema bounds are compared from their exact JSON tokens rather than
  narrowed to CLR `decimal` or binary Float, including bounded scientific
  notation such as `1e100`.

The in-memory store is useful for embedded sessions and tests. A durable store
must validate snapshots, integrity evidence, and recovery state before
publication. A game with an authoritative database can implement
`IWorkflowRunStore`.

## Durable agent stages

Register `WorkflowAgentStepExecutor` to use the built-in `agent.run` step kind.
The executor derives one nested agent run ID from the workflow run and stage
instance. Attempts, lease owners, and recovery generations do not change that
ID.

Implement `IWorkflowAgentRunAdapter` to map the stage input and settings into a
game-specific `DurableRunRequest`, continuation, resume guard, and projected
JSON output. The adapter must be deterministic for one stage instance and must
set the exact `AgentRunId` supplied in `WorkflowAgentInvocation`.

Recovery follows these rules:

1. Resume the stable nested agent run.
2. Reconcile pending game operations through the supplied reconciler.
3. If the agent is nonterminal or still reconciling, keep the workflow stage
   `Started`; do not convert partial work into success.
4. Start a new agent execution only when the durable agent journal explicitly
   reports that the derived run ID does not exist.
5. A duplicate owner leaves the stage recoverable instead of replaying work.

Completed agent output still has to pass the workflow stage's declared output
schema. Agent failure, cancellation, interruption, and budget exhaustion map to
stable workflow reason codes.

The default terminal policy is fail-closed. For an explicitly optional branch,
the game adapter may also implement
`IWorkflowAgentTerminalOutcomeProjector`. Returning `true` converts that
terminal outcome into a normal local fallback, which must still pass the stage
output schema; returning `false` preserves failure. Only game code can decide
whether a failed simulation branch is optional, so the workflow core never
invents a fallback.

## Authority boundary

A workflow composes work; it is not world authority. An executor that changes
game state must call a durable, idempotent host boundary with a stable operation
ID and must reconcile an unknown result before retrying. Provider completion
order never grants mutation order. The game remains responsible for gameplay
legality, conflicts, settlement policy, and which workflow outputs may enter an
authoritative world transaction.

## Model-authored command plans

`GeneratedPlanCompiler` exposes a stricter generated JSON surface over the
workflow kernel. The game supplies a closed catalog of command argument,
changing execution-input, and result schemas. Generated plans may compose
command, bounded foreach, reduce, and bounded loop stages, while dependencies
provide sequential or parallel execution.

The generated document cannot declare executors or executable code. Unknown
commands, properties, pointers, dependency cycles, malformed arguments, and a
worst-case expanded execution count above
`GeneratedPlanAdmissionOptions.MaxExpandedStageExecutions` are rejected before
the workflow starts. A foreach or loop additionally requires a host-owned
execution-input schema; model content cannot define that trust boundary.

Register `GeneratedPlanStepExecutor` with an
`IGeneratedPlanCommandHost`. Every invocation carries a stable `ExecutionId`.
The host must persist its side-effect receipt under that ID and implement
`TryGetReceiptAsync`; recovery checks the receipt before considering another
execution. A host command may also use the durable external-attention
coordinator to pause for a player or game signal and return its resolution on
recovery.
