# Unity host integration

## Architecture

```text
UnityAgentRuntimeHost (MonoBehaviour)
  -> UnityAgentRuntimeFacade
     -> IUnityDurableAgentRuntimeBackend
        -> shared IDurableAgentRuntime

validated ActionRequest
  -> UnityMainThreadGameHost / UnityMainThreadDispatcher
     -> game-owned handler
        -> authoritative ActionReceipt
```

The adapter owns Unity lifecycle, queue pumping, cancellation, and thread
affinity. Protocol, Agent Loop, persistence, tools, skills, memory, budgets,
operation identifiers, and receipts stay in engine-neutral assemblies.

## Composition checklist

1. Choose a streaming provider and credential source.
2. Define an agent profile, typed observations, tools, and optional skills.
3. Register game action handlers. Mark handlers that touch Unity objects for
   main-thread execution.
4. Choose durable session and memory stores under a game-owned data directory.
5. Configure budgets, provider routes, retries, final-output admission, and
   metrics.
6. Build one `BuiltGameAgentRuntime` composition for the intended lifetime.
7. Configure one `UnityAgentRuntimeHost` before starting runs.
8. On shutdown, stop admission and await the host drain before destroying its
   GameObject.

## Execution surfaces

With a `BuiltUnityAgentRuntimeBackend`, the host exposes:

- `RunAsync` for normal durable Agent work;
- `RunRoutedAsync` for bounded hybrid automatic Direct/Agent/Workflow
  selection from structured signals and the latest normalized user input;
- `CompleteAsync` for stateless single-provider-turn work;
- `RunChildAsync` and `CancelChildren` for bounded delegation.
- optional `SubmitGenerationAsync`, `RefreshGenerationAsync`,
  `WaitForGenerationAsync`, and `CancelGenerationAsync` after
  `ConfigureGeneration`.

`ModelInferenceOptions` and `ProviderRoutePreference` remain shared core DTOs,
so reasoning, sampling, prompt-cache, and ordered route requests behave the
same outside Unity. Child results retain durable root/parent/depth lineage. The
game still stages and adjudicates concurrent world mutations.

For delegation after restart or bounded-lineage-cache eviction, pass the
persisted parent `AgentRun` to the corresponding `RunChildAsync` overload. A
parent id alone cannot reconstruct ancestry that is no longer resident.

The **Structured Tool Loop** sample is the smallest runnable composition and
uses a deterministic provider, so it is safe to run without a network key.

Generation is provider-neutral and supports image, video, speech, and
structured-content APIs without bundling a model. Jobs preserve operation
identity, polling/cancellation state, and local artifact metadata. Subscribe to
`GenerationUpdated` and `GenerationFaulted`; inspect the job status rather than
treating an accepted asynchronous request as already materialized content.

## Threading

Provider streaming and file persistence do not run on the Unity main thread.
`UnityMainThreadDispatcher` has bounded admission and each queued operation has
a single execution claimant. `UnityAgentRuntimeHost.Update` drains work within
configured item and time budgets.

Queued work may be cancelled before it starts. Once game code begins, the game
must return the real outcome; cancellation does not prove that a side effect
did not commit.

## Structured data

`UnityProtocolBridge` converts Unity-serializable data holders and strict JSON
payloads to engine-neutral protocol DTOs. Observations may carry arbitrary
JSON, text, scalar values encoded in JSON, or bounded resource references.
Natural language is not required.

Do not expose Unity objects to the provider. Project only the context an agent
needs and enforce all permissions again inside the action handler.

## Durability and recovery

For side effects, persist the operation request before dispatch and the
authoritative receipt after completion. On restart, resume through the durable
runtime. If the previous outcome is uncertain, supply an
`IGameOperationReconciler`; do not guess or automatically replay the write.

Use state-version, timeline, clock, perspective, and entity-incarnation
coordinates to prevent stale observations from being applied to a different
save, branch, or respawned entity.

## Multi-actor work

The shared coordinator supports bounded batches with deterministic ordering and
isolated participant failures. Each participant has its own run identity and
budgets. The game decides whether and how completed decisions become one
simultaneous authoritative mutation.

## Backpressure and shutdown

Bound active runs, dispatcher capacity, events per frame, and time spent per
frame. Treat progress events as best effort. Terminal completion/fault events
have separately reserved bounded capacity, while the returned operation task
and durable store remain authoritative. Prefer `RunFaultedDetailed` for
concurrent work because it retains request identity and reconciliation status.
Completion, detailed-fault, compatibility-fault, runtime-event, and
application-pause subscribers are isolated from one another when a subscriber
throws.

The facade takes a bounded owned snapshot of mutable custom-backend requests
after active-run admission and before dispatch. Post-return caller mutation
cannot race the backend, while snapshot failure returns run and cancellation
capacity. Semantic completeness remains the injected backend's contract rather
than an adapter-imposed rule.

Terminal observers run on the Unity main thread. Their time budget is enforced
between callbacks and cannot preempt a callback already running. Every
subscriber must therefore be trusted, non-blocking, and constant-time; move
long-running work out of the observer before returning.

`ShutdownAsync` stops admission and terminal-reservation issuance, requests
cancellation, drains active runtime work, waits for every issued reservation to
publish or be abandoned, stops owned backends, and flushes owned stores.
An ordinary per-run cancellation still waiting behind saturated callbacks is
promoted onto the separately reserved shutdown lane. A single atomic gate
executes the token cancellation once, while shutdown waits for both admitted
dispatches before releasing the token source and their leases.
Published terminal notifications are retained rather than invoked from a
background shutdown thread. Later main-thread `Update` calls continue draining
them after shutdown; for controlled teardown, await `ShutdownAsync`, then keep
the host alive until `PendingTerminalObserverCount` is zero. If Unity destroys
the host or tears down the process before that drain, the returned task and
durable store remain authoritative and the next launch must use normal durable
recovery.

## Deployment security

- Do not ship a reusable provider secret in a client.
- Prefer player-owned credentials or short-lived scoped service tokens.
- Keep tool schemas narrow and validate again in game code.
- Treat model output, imported text, and provider error bodies as untrusted.
- Keep save/state mutations behind authoritative handlers.

## Verification levels

The package ships gates for managed compilation, artifact contents, assembly
loading, host lifecycle, and protocol conformance. A licensed Unity Editor is
required for the provided Mono/IL2CPP build-and-run gate. The release
verification environment executed the EditMode, PlayMode, Mono Player, and
IL2CPP Player paths with Unity 6000.5.6f1 on Windows. Both Players completed the
durable tool-loop marker scenario and exited successfully.
