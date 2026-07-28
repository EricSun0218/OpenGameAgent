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
