# Contributing

This project is in Alpha. Open an issue before a large API change so protocol
and engine compatibility can be reviewed together.

Questions belong in GitHub Discussions. Use the bug or feature issue forms for
actionable repository work. Security reports follow [SECURITY.md](SECURITY.md),
and conduct reports follow [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

By intentionally submitting a contribution for inclusion, you license that
contribution under Apache License 2.0 as described by section 5 of
[LICENSE](LICENSE), unless you explicitly state otherwise with the submission.
Only submit work that you have the right to license. No contributor license
agreement is currently required.

## Development

```powershell
dotnet restore GameAgentRuntime.sln
dotnet test GameAgentRuntime.sln -c Release --no-restore
dotnet format GameAgentRuntime.sln --verify-no-changes --no-restore
./engines/shared/Test-ReleaseVersionConsistency.ps1
./engines/shared/Test-ReleaseVersionConsistencySelfTest.ps1
./engines/shared/Test-TrustedWorkflowContract.ps1 -SelfTest
./engines/shared/Test-ReleaseArtifactPrivacySelfTest.ps1
./engines/shared/Test-TrackedSourcePrivacySelfTest.ps1
./engines/shared/Test-NuGetDependencyHealth.ps1
./engines/shared/Write-DeterministicDirectoryArchiveSelfTest.ps1
git diff --check
```

Changes to wire DTOs must include:

- an updated JSON Schema;
- semantic validation;
- positive and negative fixtures;
- generated serialization metadata;
- compatibility tests for Godot and Unity.

Changes to side-effect execution must include a crash, cancellation, or
reconciliation test. A successful demo is not a substitute for these tests.

Pull requests must explain the affected authority boundary and include the
smallest relevant verification evidence. Maintainers may ask for an issue first
when a change affects public API, protocol, persistence, security, packaging,
or verified engine behavior. Keep unrelated refactors out of the same pull
request.

When a product fact, version, compatibility claim, setup step, or support link
changes, update both `README.md` and `README.zh-CN.md` in the same pull request.

Do not commit credentials, generated engine state, local journals, provider
responses, proprietary game assets, or private design material.

CI scans every tracked path, raw Git blob, package, and nested ZIP payload with
bounded expansion, independently of export attributes. Additional release deny
expressions are supplied through the `GAME_AGENT_RELEASE_DENY_REGEX` repository
secret, one regular expression per line. After ordinary CI, trusted
default-branch code scans the exact candidate SHA and downloaded artifacts;
candidate code is never executed with that secret. The scanner never prints
expressions or matched content.

See [GOVERNANCE.md](GOVERNANCE.md) for decision-making and
[SUPPORT.md](SUPPORT.md) for the public support boundary.
