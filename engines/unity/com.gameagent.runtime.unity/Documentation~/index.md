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

The **Structured Tool Loop** sample is the smallest runnable composition and
uses a deterministic provider, so it is safe to run without a network key.

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
frame. Treat progress events as best effort; durable outcomes and stores are
authoritative.

`ShutdownAsync` stops admission, requests cancellation, drains active callbacks,
stops owned backends, and flushes owned stores. If Unity tears down the process
before completion, the next launch must use normal durable recovery.

## Deployment security

- Do not ship a reusable provider secret in a client.
- Prefer player-owned credentials or short-lived scoped service tokens.
- Keep tool schemas narrow and validate again in game code.
- Treat model output, imported text, and provider error bodies as untrusted.
- Keep save/state mutations behind authoritative handlers.

## Verification levels

The package ships gates for managed compilation, artifact contents, assembly
loading, host lifecycle, and protocol conformance. A licensed Unity Editor is
required for the provided Mono/IL2CPP build-and-run gate. This alpha does not
claim that Editor gate as executed in the repository verification environment.
