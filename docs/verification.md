# Verification boundaries

OpenGameAgent separates the portable TypeScript/package gate from editor and native-engine acceptance so that a green
result has one precise meaning.

| Gate | Environment | Evidence |
| --- | --- | --- |
| `npm run gate` | Node.js from `.nvmrc` on the current host | Generated artifacts, formatting, lint, types, tests, compiled local example and media smoke, source-package metadata and license notices, dependency audit, and portable Unreal structure |
| `.github/workflows/linux-gate.yml` | GitHub-hosted Ubuntu 24.04 with the repository's exact Node.js and npm versions | The complete portable gate, including Linux native dependency installation and Linux process behavior |
| `npm run full:gate` | A configured development workstation with every supported editor present | The portable gate plus real Unity, Godot, and Unreal editor/native integration acceptance |

The Linux job does not claim to run Unity, Godot, or Unreal. A Windows engine-editor run does not substitute for the Linux
package gate.

## Package metadata

`npm run package:check` first enforces the common source-workspace contract and then dry-packs every workspace. All
`@opengameagent/*` packages use MIT, include the repository's license notice, and remain ESM-only, version-aligned,
Node-engine-aligned, and internally dependency-aligned. Their `private: true` flag guards against unintended npm
publication; source use and redistribution follow the MIT license. Package-specific export additions used by integrations
and development tools remain allowed.

## Engine prerequisites

- Unity acceptance requires one or more Unity 6 (`6000.x`) editors configured through `OGA_UNITY_EDITORS` or
  `OGA_UNITY_EDITOR`.
- Godot acceptance requires .NET 8 and a Godot editor configured through `OGA_GODOT_EDITOR` or available on `PATH`.
- Unreal acceptance targets Unreal Engine 5.8 Win64 x64 and requires `UNREAL_ENGINE_ROOT` plus an explicit
  `OGA_UNREAL_GATE_TIMEOUT_MS` host budget, or the equivalent command-line arguments described in
  `engines/unreal/README.md`.

The engine-specific READMEs are authoritative for their transport contracts and focused commands.
