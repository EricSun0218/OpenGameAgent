# Durable workflows

`GameAgent.Workflow` is an optional composition plane for bounded, recoverable
jobs. The agent runtime and interactive-world evaluator do not depend on it.
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

## Authority boundary

A workflow composes work; it is not world authority. An executor that changes
game state must call a durable, idempotent host boundary with a stable operation
ID and must reconcile an unknown result before retrying. Provider completion
order never grants mutation order. The game remains responsible for gameplay
legality, conflicts, settlement policy, and which workflow outputs may enter an
authoritative world transaction.
