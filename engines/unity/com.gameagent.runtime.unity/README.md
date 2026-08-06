# OpenGameAgent for Unity

This package hosts the shared C# Agent Runtime inside Unity. It owns Unity
lifecycle and thread affinity while the shared assemblies own Agent Loop,
protocol, persistence, budgets, tools, skills, and recovery semantics.

## Requirements

- Unity 2022.3 LTS or newer.
- API Compatibility Level: .NET Standard 2.1.
- Mono or IL2CPP scripting backend.

## Install

This repository directory is a source template. First build the complete local
UPM artifact:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  engines/unity/scripts/Build-UpmPackage.ps1 -Force
```

Then install this folder through Package Manager:

```text
engines/unity/artifacts/com.gameagent.runtime.unity
```

The artifact includes the shared runtime, optional durable Workflow and
Generation modules, OpenAI-compatible and Anthropic streaming adapters, and a
media HTTP adapter. It contains no models, credentials, or game-specific tools.

## Integrate

Add `UnityAgentRuntimeHost` to a persistent GameObject. At application startup:

1. Get or create the persistent `UnityAgentRuntimeHost`.
2. Construct the game-owned `IGameHost` with `host.Dispatcher`.
3. Compose a `BuiltGameAgentRuntime` with `GameAgentRuntimeBuilder`.
4. Call `host.Configure(...)` exactly once.
5. Start `DurableRunRequest` instances through `RunAsync`.
6. Pump and display runtime events without treating streamed text as an
   authoritative state mutation.
7. Await `ShutdownAsync` during controlled shutdown when possible.

Do not create a separate dispatcher for the game host. The runtime host pumps
the bounded dispatcher exposed by its `Dispatcher` property.

Import the **Structured Tool Loop** sample from Package Manager for an offline,
deterministic example. It sends structured JSON context, receives a streamed
tool call, executes a journaled Unity-main-thread action, and produces final
structured output without network access.

For media or structured-content APIs, compose a `GenerationRuntime`, call
`host.ConfigureGeneration(runtime)`, then use `SubmitGenerationAsync`,
`RefreshGenerationAsync`, `WaitForGenerationAsync`, or
`CancelGenerationAsync`. `GenerationUpdated` and `GenerationFaulted` are posted
through the bounded Unity event path. The shared runtime accepts local or
remote providers and bundles no generation model.

## Action authority

Game handlers receive validated `ActionRequest` values and return
`ActionReceipt` values. The receipt status is the authority boundary:

- `succeeded`: the game confirms the mutation;
- `rejected`: the request was legal to process but not admitted;
- `failed`: execution failed with a known outcome;
- `unknown`: the runtime must not blindly repeat a possibly committed write.

Use engine-main-thread affinity for Unity object access and narrow conflict
scopes for deterministic scheduling. Keep legality, permissions, and final
mutation in game code.

## Lifecycle and backpressure

`UnityAgentRuntimeHost` bounds active runs, main-thread commands, and runtime
events. Terminal completion and fault observers use a separately reserved queue,
so a burst of game-thread actions cannot drop a run's terminal notification.
`RunFaultedDetailed` includes the operation kind, known run/operation/parent
identity, and a conservative reconciliation flag; `RunFaulted` remains for
compatibility. Completion, fault, runtime-event, and application-pause
subscribers are invoked independently, so one throwing subscriber does not
suppress later observers.
`Update` drains each queue within count and time budgets. The time budget is
checked between callbacks; an individual main-thread subscriber cannot be
preempted and therefore must be trusted, non-blocking, and constant-time.

Shutdown stops new admission, cancels active work, stops the backend, and
flushes owned durable stores. It stops issuing terminal reservations first,
then waits for every reservation already issued to publish or be abandoned.
If an ordinary cancellation is waiting behind saturated callbacks, shutdown
also promotes that run onto its separately reserved shutdown lane; only one
lane executes the actual token cancellation, and both dispatches drain before
their per-run resources are released.
Those published notifications remain queued, and subsequent main-thread
`Update` calls continue to pump them even after the action dispatcher has
stopped. During controlled teardown, await `ShutdownAsync`, then keep the host
alive until `PendingTerminalObserverCount` reaches zero. The returned operation
task and durable store remain authoritative if Unity destroys the host or exits
before that drain. `IsShutdownIncomplete` reports a bounded shutdown that could
not finish cleanly.

Do not call Unity APIs from provider or persistence continuations. Route them
through `UnityMainThreadDispatcher` or a registered main-thread game host.

Every mutable request passed to an injected backend is copied into bounded
runtime-owned data after active-run admission and before backend dispatch.
Caller mutation after the facade returns cannot change the in-flight request,
and a failed snapshot releases both cancellation leases. The snapshot does not
apply core AgentRun completeness rules on behalf of a custom backend; that
backend retains validation authority.

## Credentials

Never embed a commercial provider secret in a player build. Use BYOK or a game
service that issues short-lived scoped access. The package obtains secrets only
through `IProviderCredentialSource` and does not persist them.

## Validation

The repository provides managed compile, package-layout, artifact-load,
lifecycle, and conformance gates. `Invoke-UnityEditorGate.ps1` adds real
Editor/Player validation when a licensed Editor is available. Licensed Unity
6000.5.6f1 on Windows passes the EditMode, PlayMode, Mono Player, and IL2CPP
Player paths; both Players complete the durable tool-loop marker gate.

See [Documentation~/index.md](Documentation~/index.md) for the architecture and
integration checklist.

The built runtime backend also exposes `RunRoutedAsync`, `CompleteAsync`,
`RunChildAsync`, and `CancelChildren` through `UnityAgentRuntimeHost`. These use
the shared hybrid automatic routing, per-operation inference/provider
selection, and bounded child-lineage contracts rather than Unity-specific
Agent behavior. Obvious dialogue stays on the one-turn Direct path; actionable
or structured input retains Agent capabilities, and explicit requirements or
per-run model choices always win.
Use the `RunChildAsync(AgentRun, ...)` overload when the parent was restored
from durable storage or delegation continues after supervisor cache eviction;
the string overload is intended for roots or currently supervised parents.
