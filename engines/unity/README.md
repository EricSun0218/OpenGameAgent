# Unity host

The Unity integration is a release-built UPM package that hosts the shared C#
Agent Runtime in the Unity process. It does not implement a second Unity-only
Agent Loop.

- source template: `com.gameagent.runtime.unity`;
- builder: `scripts/Build-UpmPackage.ps1`;
- assembled artifact: `artifacts/com.gameagent.runtime.unity`;
- no-Editor gate: `scripts/Test-UnityPackage.ps1`;
- licensed Editor/Player gate: `scripts/Invoke-UnityEditorGate.ps1`.

Build and verify the local UPM artifact:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File engines/unity/scripts/Build-UpmPackage.ps1 -Force

powershell -NoProfile -ExecutionPolicy Bypass `
  -File engines/unity/scripts/Test-UnityPackage.ps1
```

Install `engines/unity/artifacts/com.gameagent.runtime.unity` through Unity
Package Manager. Publish the assembled artifact, not the source template.

The default artifact omits managed symbols. Pass `-IncludeSymbols` to produce
and verify a matching portable-PDB set.

The package includes Core, Protocol, Persistence, Runtime, Workflow,
Generation, both built-in streaming provider adapters, and the media HTTP
adapter. The game supplies credentials, tools, state, rules, and authoritative
action handlers.

`UnityAgentRuntimeHost` exposes durable `RunAsync`, `RunRoutedAsync`, stateless
`CompleteAsync`, bounded `RunChildAsync`, and `CancelChildren`. Per-operation
reasoning, sampling, prompt-cache, and provider-route controls travel on the
shared request DTOs. Child results use the same durable completion event path;
the game remains responsible for simultaneous-action resolution.

Call `ConfigureGeneration` to attach an optional `GenerationRuntime`. The host
then exposes `SubmitGenerationAsync`, `RefreshGenerationAsync`,
`WaitForGenerationAsync`, and `CancelGenerationAsync`, plus
`GenerationUpdated` and `GenerationFaulted` main-thread events. Media APIs may
be local or remote; no generation model is included in the package.

Current evidence covers managed compilation, package structure, artifact
loading, lifecycle tests, and host conformance. Licensed Unity 6000.5.6f1 on
Windows also passes 7 EditMode tests, 3 PlayMode tests, and the Mono and IL2CPP
Player gates. Each Player is built, launched headlessly, required to complete
the durable tool-loop scenario and validated marker, and then required to exit
successfully.

See the [package README](com.gameagent.runtime.unity/README.md) and
[integration documentation](com.gameagent.runtime.unity/Documentation~/index.md).
