# Tools, skills, and memory

## Tools

Tools are registered as immutable snapshots. A turn captures one snapshot; a
registry replacement affects only later turns.

`ToolDescriptor.visibility` is `direct`, `deferred`, or `internal`. Direct tools
are sent to the provider on every authorized turn. Internal tools never enter
model-facing search, activation, or provider schemas. Deferred tools use a
bounded two-step disclosure flow:

1. `runtime_tool_search` searches only deferred descriptors authorized by the
   current run's `IToolDisclosurePolicy`. Results contain a small summary plus
   exact `name`, `version`, descriptor digest, and source identity.
2. `runtime_tool_activate` accepts that exact identity. A successful activation
   becomes callable on the next provider turn through the ordinary provider
   schema, argument validator, scheduler, write-ahead action request, and host
   receipt path. It is not a generic proxy. Calling the newly activated tool in
   the same provider response still returns `unknown_tool`.

The default disclosure policy permits registered deferred tools. A custom
policy is installed with
`GameAgentRuntimeBuilder.WithToolDisclosurePolicy(...)`; exceptions and invalid
decisions fail closed. Search is deterministic for Unicode text and bounded by
`DurableAgentRuntimeOptions.ToolDisclosureLimits`, which also caps activated
tools and control calls per turn. The two reserved control names cannot be
registered as game tools.

Activation state is isolated per run and durably stores the exact
`name@version`, descriptor digest, source, and origin. Recovery never retargets
that state. If the descriptor disappears, changes without a matching digest, or
fails policy revalidation, the activation is revoked and the removal is
persisted before provider dispatch. An explicit activation of the new exact
identity is required.

`retryPolicy` and `idempotencyPolicy` are validated contract metadata. In this
alpha, they do not cause the runtime to automatically repeat a game tool or host
action. Provider-attempt retry is a separate mechanism. `timeoutMs` must be from
1 through 86,400,000.

The runtime validates model arguments against a bounded strict JSON Schema
subset before game code is called. Supported keywords include:

- `type`
- `properties`
- `required`
- boolean `additionalProperties`
- `enum` and `const`
- numeric `minimum` and `maximum`
- `minLength` and `maxLength`
- `items`, `minItems`, and `maxItems`

Unsupported keywords fail closed. Error objects contain codes and JSON paths,
never the rejected values.

Successful host results are checked against `resultSchema` when one is declared.
If the host committed an action but returned a nonconforming result, the runtime
preserves the authoritative success status, removes the invalid payload, marks it
non-retryable, and reports `tool_result_schema_invalid` to the next model turn.

Conflict scopes may reference validated argument fields:

```text
entity:{entityId}
inventory:{owner.id}
```

Values are UTF-8 bounded and percent-encoded. Trusted runtime bindings such as
`agentId`, `worldId`, `runId`, and `turnId` cannot be spoofed by model arguments.

### Timed-out host executions

A timeout bounds the agent loop, but it cannot forcibly stop arbitrary host
code. A durable action deadline starts before its write-ahead journal append.
Journal, queue, parallelism, effect-barrier, and conflict-lock wait all consume
that same absolute deadline. If it expires before host dispatch, the scheduler
returns `tool_deadline_expired` with `MayHaveExecuted = false`. When a dispatched
host executor ignores cancellation, the scheduler returns `tool_timeout` and
keeps the original parallelism, conflict, and effect leases until that executor
actually finishes. It also places the execution in a temporary quarantine:

The timeout wait is established before host dispatch. If an injected timeout
service fails synchronously, the scheduler returns
`tool_timeout_infrastructure_exception` with `MayHaveExecuted = false` and does
not invoke the host.

- the same exact tool name and version is rejected before host dispatch with
  `tool_dispatch_blocked_by_detached_execution`;
- calls that can conflict with a detached side effect are rejected with
  `tool_dispatch_blocked_by_detached_side_effect`;
- a detached world or external write is a global barrier, so every later tool
  call is rejected until it finishes;
- a detached agent-local write blocks later writes and reads with overlapping
  resolved conflict keys, while unrelated pure reads may still run.

These failures have `MayHaveExecuted = false`. Quarantine is removed
automatically only after the detached executor completes; late exceptions are
observed and never replace the already returned timeout.

`ToolBatchScheduler.DetachedExecutionCount` and
`GetDetachedExecutionSnapshot(...)` expose a bounded diagnostic census with
identity, effect, reason, and timing only; arguments and results are never
retained. `ToolSchedulerLimits.MaxDetachedSnapshotItems` caps each snapshot.
`DrainDetachedExecutionsAsync(timeout, cancellationToken)` requires a finite
timeout and lets a host wait for quiescence without making shutdown unbounded.
Stop new run admission before draining, because the drain signal describes the
currently quarantined set and does not prevent new tool work.

The standard runtime shutdown does this automatically: it closes new-run
admission, cancels and drains active runs, then waits for detached tool work for
at most `ToolSchedulerLimits.DetachedShutdownDrainTimeoutMs` (1,000 ms by
default, configurable from 0 through 60,000). A timeout never makes shutdown
wait forever; journal flush and owned-resource disposal continue while the
remaining executor stays quarantined until it actually returns. Such host code
cannot be forcibly terminated by the runtime. Standard-builder users can
inspect `DurableAgentRuntime.DetachedToolExecutionCount`,
`GetDetachedToolExecutionSnapshot(...)`, and the nullable
`DetachedToolExecutionsDrainedOnStop` result.

Complete conflict keys are part of the game's tool contract; incomplete keys
can make scoped concurrency unsafe.

### Semantic no-progress guard

The durable runtime enables a bounded semantic tool-loop guard by default.
`DurableAgentRuntimeOptions.ToolLoopGuard` controls it. The first stable outcome
establishes a baseline; the default warning appears after two identical
repetitions, and the run fails with `tool_no_progress` after four identical
repetitions. Both thresholds count repetitions after the baseline.

The guard compares the canonical tool name and arguments together with the
captured tool version, effect, and descriptor digest. Those optional identity
fields are stored on the durable assistant tool-call record, so recovery uses
the descriptor that the model actually saw instead of retargeting history to
the current catalog. Older records remain readable.

Only two cases accumulate:

- the same terminal failure or rejection for the same call signature;
- the same successful result from a captured `pure_read` tool.

A successful write, a non-null state diff, authoritative observations, a
revision change, or a changed read result is progress and resets accumulated
patterns. Unknown, pending, malformed, oversized, or legacy successful results
without captured effect evidence fail open and never cause a semantic stop.
Normal `AgentBudget.MaxTurns`, `MaxActions`, token, cost, and duration limits
still bound those fail-open paths.

Warnings are durable user messages added to the next provider turn. They contain
only the tool name, canonical digests, repetition count, and stable reason code;
they never copy tool arguments or results. At the hard threshold the runtime
commits the failed run before charging or dispatching another provider turn.
After a process loss at the preceding clean turn boundary, the guard rebuilds
from the bounded durable transcript and reaches the same decision without
replaying the action.

## Skills

A skill manifest contains:

- versioned identity and digest;
- description and prompt fragments;
- required and optional tools;
- context-provider references;
- resource references;
- capability requirements, trust, and activation policy.

The runtime sends a bounded catalog plus full data only for activated skills.
Catalog and prompt budgets are independent. Skill and tool registries expose
monotonic generations and canonical digests.

Skill prompt fragments are privileged system input, so activation passes
through `ISkillAdmissionPolicy` before a provider request is built. The default
policy is deliberately narrow:

- only `builtin` and `trusted` skills can be disclosed;
- full skill content is used only when the exact skill version appears in the
  effective run or continuation `ActiveSkills` list;
- `capabilityRequirements` and `activationPolicy` must be empty objects because
  the default policy does not pretend to enforce application-specific fields;
- every `requiredToolRef` must use `name@version` and match that exact version
  in the immutable tool snapshot captured for the same turn.

An explicitly active skill may use a matching direct required tool immediately.
A deferred required tool is auto-activated for that same provider turn only
when disclosure policy permits it and the activation cap can admit the entire
required set. Internal tools, policy denial, a stale descriptor digest, or
capacity exhaustion fail the skill before provider dispatch with a stable
reason code. Optional tool references never auto-activate.

`ResumeAsync(runId)` inherits the latest durable active-skill disclosure so a
process restart or a crash at a clean turn boundary does not silently remove
instructions. A non-empty continuation `ActiveSkills` collection replaces that
activation. To explicitly deactivate every skill, pass an empty collection and
set `DurableRunContinuation.ReplaceActiveSkills` to `true`.

An untrusted or otherwise unsupported inactive skill is omitted from the system
catalog. Requesting it as active fails the run with a stable skill-admission
reason code before provider dispatch. Required-tool presence and exact version
matching are runtime invariants and cannot be bypassed by a custom policy.

Games that can actually evaluate trust, capabilities, or activation rules may
implement `ISkillAdmissionPolicy` and inject it with
`GameAgentRuntimeBuilder.WithSkillAdmissionPolicy(...)`. A custom policy can
explicitly admit declarations that the default does not interpret and records
its own stable allow/deny reason. Policy evaluation should be deterministic,
bounded, and non-blocking; the request exposes immutable turn identities and
the captured skill and tool data. Each durable `TurnSnapshot` stores the policy
identity and version, an admission digest, and admitted active-skill decisions
with their content digests.

The `toolDisclosure` turn-snapshot extension records the disclosure policy
identity/version, base direct descriptors, active deferred identities, the
effective provider-tool digest, the digest of currently authorized but still
hidden deferred descriptors, durable state digest, and decision/reason digests.
All entries come from the immutable catalog captured for that turn.

## Memory

`IMemoryProvider` is the retrieval boundary. `IMemoryStore` adds upsert and
delete operations.

`DeterministicMemoryStore` is a bounded local baseline:

- no embedding model is required;
- strings and structured JSON are tokenized deterministically;
- queries are scoped and may require tags;
- expired records are ignored;
- result count and UTF-8 bytes are bounded;
- ranking is stable.

Games can replace it with vector, full-text, graph, database, or hosted memory
without changing the agent loop. The game maps retrieved records to
`ContextCandidate` values before starting or steering a run. They then enter the
same context compiler as observations, so one budget controls all prompt input
and retrieval policy stays explicit.
