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

The package includes Core, Protocol, Persistence, Runtime, Workflow, and both
built-in streaming provider adapters. The game supplies credentials, tools,
state, rules, and authoritative action handlers.

Current evidence covers managed compilation, package structure, artifact
loading, lifecycle tests, and host conformance. A real Unity Editor/Player gate
is available but has not been executed for this alpha because no licensed
Editor is present in the verification environment.

See the [package README](com.gameagent.runtime.unity/README.md) and
[integration documentation](com.gameagent.runtime.unity/Documentation~/index.md).
