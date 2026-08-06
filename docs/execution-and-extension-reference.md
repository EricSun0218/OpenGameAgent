# Execution and extension reference

This reference covers the execution paths and the bounded extension surfaces
that sit around the durable Agent Loop. For a first integration, start with
[Getting started](getting-started.md).

## Execution surfaces

`BuiltGameAgentRuntime` exposes four related surfaces:

| Surface | State | Model turns | Tools and skills | Intended use |
| --- | --- | --- | --- | --- |
| `Completion` | Stateless | One | No | Classification, extraction, rewriting, or other isolated model calls |
| `Execution` with `Direct` | Durable | One | Hidden | Dialogue or generation that needs journal, memory, recovery, and accounting but no action loop |
| `Execution` with `Agent` | Durable | Bounded loop | Yes | NPC decisions, directors, assistants, and game actions |
| `Execution` with `Workflow` | Workflow-owned | Defined by workflow | Per step | Fixed, recoverable orchestration around Agent steps |

`Completion` deliberately creates no session, journal entry, memory write, or
tool call. `Direct` is not stateless: it uses the same durable input, context,
memory, provider resilience, accounting, and recovery contracts as `Agent`, but
exposes no tools or skills and ends after one provider response.

### Hybrid automatic routing

`RoutedExecutionRuntime` accepts an `ExecutionRouteRequest`. The default
`AutomaticExecutionRoutePolicy` first applies immutable requirements:

- `Workflow` when `ExecutionRequirements.Workflow` or `ParallelActors` is
  present; multi-actor work must use a workflow that coordinates participants;
- `Agent` for tools, skills, durable effects, or multiple model turns;
- `Direct` remains the minimum path when none of those capabilities is
  required.

It then combines the bounded structured `Signal` with the latest normalized
user input. Short scalar dialogue stays on `Direct`; actionable terms,
structured or multipart input, and long input select `Agent`. Intermediate text
is ambiguous and conservatively selects `Agent` unless an optional
`IAutomaticExecutionClassifier` returns a valid, sufficiently confident
decision. Obvious cases never pay for a classifier call. A workflow hint may
select `Workflow` only when a workflow payload is present.

The same policy can attach a `DirectModelProfile` or `AgentModelProfile` with
provider-route and inference defaults. Explicit `Inference` and
`RoutePreference` values on the durable run win independently, so automatic
selection never replaces a caller override.

An explicit path is validated against the requirements. A custom
`IExecutionRoutePolicy` receives an optional bounded structured `Signal`.
Policy execution is concurrency-limited and timed out. The automatic policy
uses its local conservative result when its optional classifier fails or times
out. Other failed, timed-out, or invalid custom policies use the least-capable
deterministic path that satisfies immutable requirements. Configure the built-in
policy with `WithAutomaticExecutionRouting(...)`, or replace it through
`WithExecutionRoutePolicy(...)`.

A workflow route additionally requires an `IRoutedWorkflowRuntime`. The
`GameAgent.Workflow.RoutedWorkflowRuntime` binds immutable compiled workflows by
ID and can be registered with `WithRoutedWorkflowRuntime(...)`.

### Injected backend request boundary

Engine facades reserve run and cancellation capacity, then take a bounded,
runtime-owned snapshot before dispatching to an injected backend. Collections,
strings, extension JSON, transcript data, and aggregate payload size are
checked before caller-owned mutable state can cross the asynchronous boundary.
Snapshot failure returns the reserved capacity. Structural limits remain a
runtime invariant, while semantic completeness of an `AgentRun` remains the
custom backend's contract; choosing an injected backend does not silently opt
it into the built-in durable runtime's validation policy.

## Per-operation model controls

`DurableRunRequest` and `SimpleCompletionRequest` accept:

- `ModelInferenceOptions Inference`;
- `ProviderRoutePreference RoutePreference`.

Inference controls are provider-neutral and apply to one operation:

| Property | Meaning |
| --- | --- |
| `ReasoningEnabled` | Enable or explicitly disable reasoning when the route supports it |
| `ReasoningEffort` | `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, or `max` |
| `ReasoningTokenBudget` | Explicit reasoning-token budget |
| `Temperature` / `TopP` | Mutually exclusive sampling controls |
| `Seed` | Deterministic seed where supported |
| `PromptCachingEnabled` | Request caching or provider default; explicit bypass is rejected unless an adapter has an exact wire mapping |
| `PromptCacheKey` | Stable non-secret cache bucket |
| `PromptCacheRetention` | `5m` or `1h` |

A provider must map every explicitly requested control or reject the request
before transport; controls are never silently discarded. Provider route
preferences contain an ordered list of configured provider IDs and an explicit
`AllowUnlistedFallback` switch. The selected route still obeys capability,
cooldown, retry, budget, and stale-stream fencing.

Inference and route preferences are captured in durable run input. Resume uses
the captured values instead of silently adopting a new caller preference.

## Lifecycle middleware

Implement `IAgentLifecycleMiddleware` to observe or guard typed lifecycle
events:

- `run_starting` and `run_completed`;
- `model_dispatching` and `model_completed`;
- `tool_batch_dispatching` and `tool_batch_completed`.

Register middleware through `WithLifecycleMiddleware(...)`. A registration is
required by default. Required middleware may reject a before-event with
`AgentLifecycleDecision.Reject(...)`; timeout, failure, or rejection stops the
protected operation. Optional middleware is fail-soft and is suitable for
telemetry or derived analytics.

Callbacks run behind bounded concurrency, per-callback timeout isolation, and
an invocation-wide `PipelineTimeout`; a long optional chain cannot multiply
latency without bound. Model-dispatch middleware completes before a scarce
provider-workload lease is acquired. Event objects are typed snapshots rather
than mutable runtime objects. For resume, `run_starting` is emitted only after
durable recovery and carries the recovered `AgentId`, `WorldId`, explicit
`SessionId`, and validated `GameContext`; required admission middleware
therefore sees the same identity boundary as a new run before ownership,
provider, reconciler, or host work.
The synchronous prefix of each middleware callback starts outside the ordinary
.NET worker pool under the shared process callback bound. A required callback
that cannot obtain this execution capacity fails closed with
`middleware_execution_capacity_exhausted`; an optional callback is skipped.
Middleware must not be used as the game's final rule authority: tool handlers
still validate current state and return the authoritative `ActionReceipt`.

## Provider callback execution

Provider stream construction, iterator construction, each stream-advance call,
the current-event getter, request/wire preparation, and provider-owned stream
disposal use the same bounded prefix isolation. Capacity failure before
dispatch is reported as
`provider_execution_capacity_exhausted` with known-zero usage; after streaming
has begun, usage remains uncertain and follows normal reconciliation rules.
Once admitted to the bounded outstanding-callback budget, provider-owned
disposal waits for transient active-prefix saturation and remains covered by
the normal cleanup timeout and detached-cleanup quarantine. If that process-wide
outstanding budget is itself exhausted, cleanup fails explicitly and the
provider remains quarantined instead of adding an unbounded waiter.

## Conversation context

The built-in context manager provides request preparation, pruning, derived
compaction, token estimation, and durable evidence. A game that needs a
different policy can implement `IConversationContextEngine` and register it
with `WithConversationContextEngine(...)`.

A custom context engine and the built-in conversation compactor are mutually
exclusive. Custom engines run behind bounded concurrency, deadlines,
cancellation, and tracked shutdown cleanup. They receive runtime-owned input;
their output is re-snapshotted and remeasured, and their report is recomputed.
Stable and system messages must remain byte-equivalent and in input order,
reused message IDs cannot change content, and arbitrary synthetic user,
assistant, system, tool-call, or tool-result messages are rejected. The only
new message shape admitted is one strictly validated, low-authority historical
summary envelope whose source identifiers belong to the admitted input. No
context engine can rewrite the authoritative durable transcript. The runtime
derives the exact omitted source set from the returned view and independently
checks its digest, source time, lineage, byte reclamation, and semantic-quality
evidence before accepting that summary.

## Memory query and ranking pipeline

`RuntimeMemoryLifecycle` can run bounded stages around memory providers:

1. `IMemoryQueryTransformer` may rewrite the semantic query payload.
2. Providers search under the resulting query.
3. Provider results are fused and deduplicated.
4. `IMemoryResultReranker` may reorder and re-score admitted records.
5. Runtime filters and byte/result limits are reapplied.

Transformers cannot broaden world, session, save revision, timeline, observer,
game-time, perspective, tag, scope, or result boundaries. Rerankers cannot add
or mutate records. Invalid, hostile, failed, or timed-out stages fall back to
the last runtime-owned snapshot.

`GameAwareMemoryReranker` combines provider score, record importance, named
game-clock recency, optional wall-clock recency, and greedy tag/source
diversity. Greedy diversity is limited to a configurable prefix (256 by
default, never less than the query result limit); the untouched tail retains
its prior deterministic order. This keeps the configured 65,536-candidate
ceiling safe for an engine process. Wall-clock recency is disabled by default
because many game worlds do not advance with real time.

## Child Agent supervision

Every built runtime exposes `Children`, a `ChildAgentSupervisor`. It provides:

- `RunChildAsync(parentRunId, request)`;
- `RunChildAsync(parentRun, request)` for recovered or already-completed
  parents whose durable lineage must be retained;
- `RunManyAsync(parentRunId, requests)` with deterministic result order and
  isolated item failures;
- `CancelChildren(parentRunId)`;
- active child count and lineage snapshots.

The supervisor bounds depth, total concurrent children, active children per
parent, batch size, remembered lineage count, child duration, and shutdown
drain. It writes
`gameAgent.childLineage` into the child run's extensions with root, parent,
child, and depth IDs. `ChildAgentLineage.Read(run)` validates and reads it.

Child Agents do not grant new game permissions. They use normal runtime tools,
budgets, receipts, persistence, and host validation. Pass the parent's
cancellation token into child calls for automatic token propagation, or call
`CancelChildren(parentRunId)` explicitly. Supervisor shutdown and child
timeouts also cancel active child work.

## Ownership and shutdown

`BuiltGameAgentRuntime.StopAsync()` stops child admission, drains child work,
stops the durable and stateless execution surfaces, flushes owned persistence,
and drains owned memory and lifecycle resources within their configured
windows. Use asynchronous shutdown; never synchronously block an engine main
thread waiting for callbacks that can re-enter the engine.
