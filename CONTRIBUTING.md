# Contributing

This project is in Alpha. Open an issue before a large API change so protocol
and engine compatibility can be reviewed together.
Repository visibility and external contribution gates are tracked in the
[pre-public release checklist](docs/pre-public-release.md).

## Development

```powershell
dotnet restore GameAgentRuntime.sln
dotnet test GameAgentRuntime.sln -c Release --no-restore
dotnet format GameAgentRuntime.sln --verify-no-changes --no-restore
./engines/shared/Test-ReleaseVersionConsistency.ps1
./engines/shared/Test-ReleaseVersionConsistencySelfTest.ps1
./engines/shared/Test-ReleaseArtifactPrivacySelfTest.ps1
./engines/shared/Test-TrackedSourcePrivacySelfTest.ps1
git diff --check
```

Changes to wire DTOs must include:

- an updated JSON Schema;
- semantic validation;
- positive and negative fixtures;
- generated serialization metadata;
- compatibility tests for Godot, Unity, and the Unreal protocol module.

Changes to side-effect execution must include a crash, cancellation, or
reconciliation test. A successful demo is not a substitute for these tests.

Do not commit credentials, generated engine state, local journals, provider
responses, proprietary game assets, or private design material.

CI scans every tracked path, raw Git blob, package, and nested ZIP payload with
bounded expansion, independently of export attributes. Additional release deny
expressions are supplied through the `GAME_AGENT_RELEASE_DENY_REGEX` repository
secret, one regular expression per line. After ordinary CI, trusted
default-branch code scans the exact candidate SHA and downloaded artifacts;
candidate code is never executed with that secret. The scanner never prints
expressions or matched content.
