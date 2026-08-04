# Pre-public release checklist

Use this as a blocking checklist. A green build alone does not authorize a
public release, and CI artifacts are not release assets until the trusted
release gates approve the exact commit.

## 1. Code, legal, and repository audit

- Confirm every tracked file is intended for public distribution and has known
  authorship or compatible licensing.
- Scan the complete reachable Git history, GitHub Actions logs, artifacts,
  issues, and pull requests for credentials, private paths, private source,
  customer data, and internal-only names.
- Confirm `LICENSE`, package license files, third-party notices, contributor
  licensing terms, `SECURITY.md`, the security model, governance, support, and
  the code of conduct are current and contain no placeholders.
- Confirm English `README.md` remains the default and that facts, versions,
  links, support status, and capability claims match `README.zh-CN.md`.
- Restore dependencies in locked mode; require zero known vulnerable or
  deprecated direct dependencies and document any accepted transitive risk.
- Run formatting, build, unit, integration, package, deterministic-output,
  source-privacy, engine-editor, and end-to-end gates from a clean checkout.

## 2. Owner approval and public-history initialization

Do not perform this section until the repository owner has reviewed the final
audit report and explicitly approved publication.

- Create a local, non-pushed recovery bundle of the private history and verify
  that the bundle is readable.
- Create one clean public root commit from the approved tree, with no parent and
  no private-history references, then force-push only `main`.
- Delete obsolete remote branches, tags, workflow runs, caches, and artifacts;
  verify the remote advertises only the intended public refs.
- Re-run CI against the new root commit and inspect the resulting logs before
  changing visibility.

## 3. GitHub public cutover

- Set the final repository description, topics, social preview, and links;
  enable Discussions and keep the wiki disabled unless it has an owner.
- Change visibility, then immediately enable private vulnerability reporting,
  Dependabot alerts and security updates, secret scanning, and push protection
  where the plan supports them.
- Apply the `main` ruleset after the visibility change: block force pushes and
  deletions, require pull requests, require conversation resolution, and
  require ordinary CI plus the unified `trusted/source-privacy` status.
- Verify workflow permissions are read-only by default, actions are SHA-pinned,
  environments and secrets are least-privilege, and untrusted pull requests
  cannot reach release credentials.
- Test anonymous clone, locked restore, build, documentation links, issue forms,
  Discussions, security reporting, and package installation from outside the
  maintainer account.

## 4. First release

- Tag only the public root or a later reviewed commit; never reuse or move a
  published tag.
- Generate checksums and a machine-readable SBOM for every release bundle.
- Produce GitHub artifact attestations for the exact uploaded assets and verify
  them from a fresh anonymous checkout.
- Publish release notes from `CHANGELOG.md`, mark the alpha as a prerelease, and
  keep immutable releases enabled.
- Do not publish to package registries until registry identities, trusted
  publishing, provenance, rollback ownership, and namespace recovery are
  configured and tested.

Do not publish packages merely because CI produced temporary artifacts.

## Deterministic package creation

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
