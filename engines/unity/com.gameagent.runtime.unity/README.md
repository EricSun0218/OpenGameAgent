# Game Agent Runtime for Unity

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

The artifact includes the shared runtime, optional durable Workflow module,
and OpenAI-compatible and Anthropic streaming adapters. It contains no
credentials or game-specific tools.

## Integrate

Add `UnityAgentRuntimeHost` to a persistent GameObject. At application startup:

1. Create a `UnityMainThreadDispatcher` and a game-owned `IGameHost`.
2. Compose a `BuiltGameAgentRuntime` with `GameAgentRuntimeBuilder`.
3. Call `UnityAgentRuntimeHost.Configure(...)` exactly once.
4. Start `DurableRunRequest` instances through `RunAsync`.
5. Pump and display runtime events without treating streamed text as an
   authoritative state mutation.
6. Await `ShutdownAsync` during controlled shutdown when possible.

Import the **Structured Tool Loop** sample from Package Manager for an offline,
deterministic example. It sends structured JSON context, receives a streamed
tool call, executes a journaled Unity-main-thread action, and produces final
structured output without network access.

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
events. `Update` drains each queue within count and time budgets. Shutdown stops
admission, cancels active work, drains callbacks, stops the backend, and flushes
owned durable stores. `IsShutdownIncomplete` reports a bounded shutdown that
could not finish cleanly.

Do not call Unity APIs from provider or persistence continuations. Route them
through `UnityMainThreadDispatcher` or a registered main-thread game host.

## Credentials

Never embed a commercial provider secret in a player build. Use BYOK or a game
service that issues short-lived scoped access. The package obtains secrets only
through `IProviderCredentialSource` and does not persist them.

## Validation

The repository provides managed compile, package-layout, artifact-load,
lifecycle, and conformance gates. `Invoke-UnityEditorGate.ps1` adds real
Editor/Player validation when a licensed Editor is available. That Editor gate
has not been executed for this alpha.

See [Documentation~/index.md](Documentation~/index.md) for the architecture and
integration checklist.
