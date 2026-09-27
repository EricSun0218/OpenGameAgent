# OpenGameAgent

[English](README.md) | [简体中文](README.zh-CN.md)

OpenGameAgent is a TypeScript runtime for AI-native game characters and worlds. It hosts a message-or-tool Agent loop while
the game remains responsible for rules, permissions, authoritative state, and world writes.

This MIT-licensed source preview contains the Pi-backed Agent loop, exact run
control, a single SQLite authority, reliable action exchange, three thin engine integrations, optional structured memory,
durable provider-response suspension and explicit resume/abandon,
durable goals/signals/schedules/mailboxes, structured player interactions, evidence-validated behavior Skill evolution,
durable text artifacts, progressive host-installed Agent Skills, declarative Agent Plugins 1.0 directories, and bounded
signed directory packaging/immutable installation, exact prepared Tool-call loop protection, MCP Tool projection,
optional deterministic visual observations,
provider-neutral durable output-media jobs, and optional bounded local VAD plus OpenAI-compatible speech adapters. It is
under active development; pin a commit when integrating it into a game.

## Quick start

Use Node.js 24.15.0 (also recorded in `.nvmrc`) and npm 11.12.1:

```sh
git clone https://github.com/EricSun0218/OpenGameAgent.git
cd OpenGameAgent
npm ci
npm run build
npm run example:local
```

The [local example](examples/local-runtime.mjs) registers one character, submits a game input, and prints its reply.
It uses a deterministic model fixture and an in-memory event store, so it needs no API key or network access.
For a real game, follow [developer integration](docs/developer-integration.md) to select a model, configure credentials
in the host, persist runtime state, and authorize Tools and Actions.

The TypeScript workspaces currently ship as source. Their `private: true` package flag prevents accidental npm
publication; it does not limit the MIT license. The versioned packages and releases from the earlier C# implementation
remain in Git history and on the Releases page; they do not describe the current TypeScript source tree.

## Architecture

- `@opengameagent/protocol` defines canonical, versioned runtime data.
- `@opengameagent/agent` adapts Pi's Agent directly to game inputs and canonical events.
- `@opengameagent/characters` compiles project-owned personas into Pi system instructions and selects deterministic,
  bounded native worldbook context without external player-content import or another Agent loop.
- `@opengameagent/realtime` coordinates bounded speech recognition, exact voice-turn interruption, canonical Pi runtime
  text, and speech synthesis without owning dialogue state, Tools, provider credentials, or a second Agent loop.
- `@opengameagent/realtime-local` adds finite or continuous state-isolated VAD endpointing, an optional hash-pinned
  Silero ONNX detector, and bounded OpenAI-compatible STT/TTS for developer-configured game audio pipelines without
  opening devices, selecting models, or adding another Agent loop.
- `@opengameagent/runtime` owns actor lifecycle and storage composition.
- `@opengameagent/storage-sqlite` owns durable authority, migrations, events, actions, approvals, and memory records.
- `@opengameagent/memory` provides game-time memory and bounded hybrid recall; `memory-usearch` is a disposable vector accelerator.
- `@opengameagent/memory-agent` optionally injects authorized, identity-bound recall as a fresh per-request Pi context snapshot.
- `@opengameagent/memory-ai-sdk` adapts remote AI SDK embedding models with explicit host retry, concurrency, and byte limits.
- `@opengameagent/memory-bge-m3` optionally runs host-supplied BGE-M3 assets in an isolated TypeScript/WASM worker.
- `@opengameagent/extensions` provides optional goals, signals, waits, authoritative game-time schedules, and immutable
  behavior Skill proposals/version rollback without introducing another Agent loop or workflow engine.
- `@opengameagent/extension-kit` gives game developers strict extension manifests, permission/dependency diagnostics,
  bounded static registration, real-runtime conformance, a buildable scaffold, and an editor-only whole-generation reload
  boundary without discovering or loading executable packages.
- `@opengameagent/artifacts` durably offloads oversized all-text Pi Tool results, exposes bounded stream-isolated reads,
  and optionally adds host-registered external knowledge sources without sending raw game input over a transport.
- `@opengameagent/delegation` runs bounded foreground or background child tasks as ordinary Pi actors in the same runtime;
  the shared SQLite authority provides fenced restart recovery without introducing a workflow engine.
- `@opengameagent/integrations` optionally projects host-installed Agent Skills through Pi's loader, discovers
  non-executing Agent Plugins 1.0 directories, builds and verifies host-signed directory packages, installs their files
  into host-owned sealed content-addressed roots, and connects explicitly admitted MCP servers through the official client
  transports, with bounded progressive disclosure, per-request selection, and game-owned call authorization. It also
  supplies bounded optional Ark image generation, Doubao bidirectional realtime speech, and a manual speech-to-durable-WAV
  media Adapter without creating another Agent, voice coordinator, or media-job authority.
- `@opengameagent/credentials-windows` optionally persists the current Pi `CredentialStore` contract with Windows
  CurrentUser DPAPI, per-write entropy, kernel-backed cross-process serialization, bounded atomic files, and reparse/path
  identity checks while provider/profile UI and release policy remain game-owned.
- `@opengameagent/png` validates bounded PNG/APNG envelopes, CRCs, structure, and animation control metadata for packages
  that need a shared zero-I/O carrier inspector without introducing another image codec.
- `@opengameagent/resources` publishes and verifies bounded immutable original bytes without path, MIME, owner, or
  media-generation lineage; hosts retain authorization and retention policy.
- `@opengameagent/attachments` optionally normalizes fully decoded still images into verified content-addressed local
  objects, projects game-authorized bytes into Pi, and can serve the same verified objects through an independently
  authorized bounded sidecar endpoint while canonical transcripts retain references only.
- `@opengameagent/observations` optionally selects authorized host snapshots under explicit model-context budgets and
  reuses the attachment store for deterministic crop/resize derivatives without owning game state or another Agent loop.
- `@opengameagent/media` provides explicit submit/read/reconcile/cancel/restart semantics for durable output generation,
  immutable result bundles, and late/partial settlement while keeping authoritative game import in ordinary Actions.

There is one Agent loop. Optional memory, planning, scheduling, actions, media, realtime, integrations, and engine adapters
will compose around it rather than replace it.

## Supported engines

- Unity 6 through the C# package under `engines/unity`.
- Godot through the C# integration under `engines/godot`.
- Unreal Engine through the C++ plugin under `engines/unreal`.

These are the complete public engine surface. C#/.NET and C++ are implementation substrates inside those integrations,
not additional engines or standalone language SDK products.

## Development

Start with [the developer integration boundary](docs/developer-integration.md) when composing the Runtime, project-owned
persona/worldbook data, shared SQLite authority, reliable Actions, and one of the three engine clients.

Use the Node.js version in `.nvmrc`. The gate bootstraps its isolated code generators from their frozen lockfiles, so a
clean checkout needs only the root install before verification:

```sh
npm ci
npm run gate
```

The portable gate is also executed on Ubuntu 24.04. Real engine-editor acceptance remains a separate host-configured
gate; see [the verification boundaries](docs/verification.md) for the exact evidence provided by each command.

### Media-size acceptance

The checked-in media workload verifies one 8192×8192 RGBA image through full decode, canonical PNG normalization,
content-addressed storage, and cold read. It also streams one 128 MiB generated resource into the neutral resource CAS,
persists its media-job bundle in SQLite, then cold-opens both stores and verifies the exact selection lineage, length, and
SHA-256 without calling the provider again.

Build the workspace before running the benchmark because the development runner intentionally exercises the compiled
package surfaces:

```sh
npm run build
npm run benchmark:media -- --config scripts/benchmark-profiles/media-size-d3d11-fl10.json --output .benchmark-data/media-size-d3d11-fl10.json
```

Every capacity and timeout is explicit in the profile. The workload values are acceptance inputs, not framework or
product defaults; reported duration and memory are host observations rather than SLAs.

## Contributing and license

Read [CONTRIBUTING.md](CONTRIBUTING.md) for development commands and the review process, and
[SECURITY.md](SECURITY.md) for private vulnerability reports. Community participation follows the
[Code of Conduct](CODE_OF_CONDUCT.md).

OpenGameAgent is available under the [MIT license](LICENSE). Third-party dependencies retain their own licenses;
see [Third-party notices](THIRD_PARTY_NOTICES.md). Engine integrations carry copies of the project license.
