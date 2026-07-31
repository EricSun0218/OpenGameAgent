# Unity host

The Unity integration is a release-built UPM package:

- template: `com.gameagent.runtime.unity`;
- package builder: `scripts/Build-UpmPackage.ps1`;
- complete local artifact: `artifacts/com.gameagent.runtime.unity`;
- no-Editor gate: `scripts/Test-UnityPackage.ps1`;
- real Unity Mono/IL2CPP build-and-run gate:
  `scripts/Invoke-UnityEditorGate.ps1`.

The package composes the shared Agent Runtime and does not maintain a second
Unity Agent Loop. See the package documentation for support claims and known
limits.

The default artifact omits managed symbols. Pass `-IncludeSymbols` to the
builder for an exact portable-PDB set matching the bundled GameAgent
assemblies; the package gate verifies both variants.

It also includes the engine-neutral world module, optional durable Workflow
module, and both the OpenAI-compatible and native Anthropic provider adapters.
`UnityNativeWorldSessionHost` owns the high-level declarative
package/runtime/save generation, while `UnityInteractiveWorldFacade` exposes
the lower-level custom-handler path. Both delegate to the same managed
implementation used by the Godot adapter. The assembled artifact includes
world-v1 schemas and an inert interactive example; games still author every
business field and rule.
