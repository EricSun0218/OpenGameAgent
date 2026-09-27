# Contributing to OpenGameAgent

Bug reports, documentation improvements, focused fixes, and engine integration contributions are welcome.
Use an issue to discuss a substantial feature or contract change before implementing it. Report security issues privately
as described in [SECURITY.md](SECURITY.md), and follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Set up

Use the Node.js version in `.nvmrc` and the npm version in the root `packageManager` field.

```sh
npm ci
npm run build
npm run example:local
npm run gate
```

The portable gate installs the isolated code generators from their lockfiles. It checks formatting, lint, generated
artifacts, types, tests, builds, media acceptance, package contents, dependency vulnerabilities, Unreal structure, and the
public source tree. See [verification boundaries](docs/verification.md) for the exact scope.

Useful focused commands:

```sh
npm run format
npm run lint
npm run typecheck
npm test -- packages/runtime/test/game-runtime.test.ts
npm run tools:ci
npm run generate
npm run generate:check
```

Edit protocol sources and generator inputs rather than generated clients. Commit the regenerated artifacts with a
contract change. Each package README describes its public surface; [developer integration](docs/developer-integration.md)
explains how the packages compose.

## Engine changes

Run the relevant `npm run unity:check`, `npm run godot:check`, or `npm run unreal:check` with its documented prerequisites.
Use `npm run full:gate` when all three engine environments are available. A successful portable gate alone does not
verify editor behavior. State the commands and engine versions tested in the pull request, including any missing coverage.

## Pull requests

- Keep a pull request focused on one problem and describe the resulting behavior.
- Include regression coverage for behavioral changes and update affected documentation and generated contracts.
- Keep secrets, machine-specific configuration, player data, and proprietary assets out of commits and issue attachments.
- Preserve license notices when adding third-party code and update [Third-party notices](THIRD_PARTY_NOTICES.md) as needed.
- Do not publish packages as part of a routine contribution; source workspaces intentionally retain `private: true`.

By submitting a contribution, you agree that your contribution is provided under the project's [MIT license](LICENSE).
