# Contributing

This project is in Alpha. Open an issue before a large API change so protocol
and engine compatibility can be reviewed together.

## Development

```powershell
dotnet restore GameAgentRuntime.sln
dotnet test GameAgentRuntime.sln -c Release --no-restore
dotnet format GameAgentRuntime.sln --verify-no-changes --no-restore
./engines/shared/Test-ReleaseVersionConsistency.ps1
./engines/shared/Test-ReleaseVersionConsistencySelfTest.ps1
./engines/shared/Test-ReleaseArtifactPrivacySelfTest.ps1
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

CI also scans a `git archive` of the exact commit. Additional release deny
expressions are supplied through the `GAME_AGENT_RELEASE_DENY_REGEX` repository
secret, one regular expression per line. The scanner never prints the
expressions or matched content.
