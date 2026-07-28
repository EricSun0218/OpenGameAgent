# Pre-public release checklist

The current Alpha repository and CI artifacts are private previews. Immediately
after the visibility change, and before accepting outside contributions:

- enable a branch rule that requires ordinary CI and the unified
  `trusted/source-privacy` commit status;
- enable and verify GitHub private vulnerability reporting;
- publish a dedicated private conduct-reporting contact;
- verify repository secrets and trusted workflows after the visibility change;
- run all engine, package, dependency, source-history, and live-provider gates
  against the exact release commit;
- create explicit package-publishing workflows only after registry identities,
  signing, provenance, and rollback ownership are configured.

Do not publish packages merely because CI produced temporary artifacts.

Release package creation uses a deterministic normalization step because the
supported .NET 8 toolchain does not produce byte-identical NuGet containers by
itself:

```powershell
dotnet pack GameAgentRuntime.sln -c Release -o artifacts/nuget-raw
./engines/shared/Write-DeterministicNuGetPackages.ps1 `
  -SourcePath ./artifacts/nuget-raw `
  -DestinationPath ./artifacts/nuget
```

Run the manifest and artifact privacy gates against `artifacts/nuget`, not the
intermediate directory.

Before privacy approval, the release pipeline persists only authenticated,
public-key-sealed staging artifacts. The public recipient certificate can seal
data but cannot recover it, and every payload is protected against
modification. The trusted release workflow seals the exact outputs exercised
by its build jobs, including both raw NuGet pack outputs. A separate runner
that never executes candidate code recovers them, performs deterministic
normalization and comparison with trusted tools, verifies the package
manifest, and applies the injected release deny policy. Failed approval
removes the recovered plaintext before the job exits. Separate runners with
no release secrets consume each privacy-approved engine or package artifact.
A final runner downloads fresh copies of the immutable approved artifacts and
publishes them without executing any candidate content.

Keep `GAME_AGENT_ARTIFACT_DECRYPTION_PFX` and
`GAME_AGENT_RELEASE_DENY_REGEX` as repository secrets. Rotate the public
recipient certificate and its private-key secret together before the
certificate expires; never commit the private key.
