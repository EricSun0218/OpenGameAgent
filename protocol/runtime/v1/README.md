# OpenGameAgent Runtime Protocol v1 artifacts

- `runtime.schema.json` is the normative JSON shape.
- `fixtures/canonical-run.jsonl` is a language-neutral reducer/conformance fixture.
- `cpp/OpenGameAgentRuntimeProtocol.hpp` contains dependency-free C++ DTOs. A native adapter maps them to its JSON library and the schema; the Unreal plugin provides the production HTTP/SSE transport.
- `typescript` contains the generated, zero-runtime-dependency TypeScript client, reducer, declarations, and packed-consumer test.
- `python` contains the generated standard-library Python client, reducer, wheel metadata, and packed-consumer test.

The C# source of truth is `OpenGameAgent.Runtime.Protocol`. Contract changes require a protocol-version decision, updated schema/fixtures/SDK types, and cross-language conformance tests. Run `tools/Generate-RuntimeProtocolSdks.ps1` after an accepted schema change and `tools/Test-RuntimeProtocolSdks.ps1` before committing. Authentication credentials are transport metadata and never part of persisted runtime events.
