# Contributing

OpenGameAgent is alpha software. For a large public API or persistence change, open an issue first so its game/runtime boundary and migration cost can be discussed.

By intentionally submitting a contribution, you license it under Apache License 2.0 as described by section 5 of [LICENSE](LICENSE), unless you explicitly state otherwise. Submit only work you have the right to license.

## Development

Install .NET SDK 8.0, then run:

```powershell
dotnet restore OpenGameAgent.sln
dotnet build OpenGameAgent.sln -c Release --no-restore
dotnet test OpenGameAgent.sln -c Release --no-build --no-restore
dotnet format OpenGameAgent.sln --verify-no-changes --no-restore
git diff --check
```

Run package or real-editor gates when changing an engine adapter; commands are in [Engine integration](docs/engine-integration.md).

Pull requests should:

- describe user-visible behavior and the affected ownership boundary;
- include focused success and failure tests;
- keep unrelated refactors separate;
- preserve stable operation identities for side effects;
- document cancellation, retry, timeout, and recovery semantics;
- add explicit limits for new external data or concurrency;
- update both English and Chinese READMEs when shared product facts change;
- contain no credentials, private prompts, player data, generated engine state, or proprietary assets.

Questions belong in GitHub Discussions. Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).
