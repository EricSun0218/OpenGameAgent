# OpenGameAgent Runtime Protocol v1

The OpenGameAgent Runtime Protocol is OpenGameAgent's optional, versioned boundary for a game engine, native client, sidecar, or self-hosted service. It is not part of `OpenGameAgent.Kernel`: in-process C# integrations can call `GameAgentRuntime` directly and omit every Runtime package.

Use it when a client must reconnect to a running agent, consume a canonical cross-language event stream, or issue race-safe control requests. The public packages are:

- `OpenGameAgent.Runtime.Protocol`: transport-neutral DTOs, JSON codec, capability negotiation, and client reducer;
- `OpenGameAgent.Runtime.Hosting`: in-process event projection and bounded replay journal;
- `OpenGameAgent.Client`: typed HTTP/SSE client;
- `OpenGameAgent.Server`: self-hostable HTTP/SSE implementation.

The normative schema, fixtures, and dependency-free C++ DTOs live under `protocol/runtime/v1`. A commercial service can consume these public artifacts, but must not fork the agent loop or copy internal server DTOs.

## Versioned distribution

Runtime Protocol v1 first ships as the `0.3.0-alpha.4` line. Pin the same exact pre-release version for `OpenGameAgent.Runtime.Protocol`, `OpenGameAgent.Runtime.Hosting`, and `OpenGameAgent.Client`; the Hosting and Client package dependencies keep their protocol package aligned.

Every GitHub Release contains `RELEASE_MANIFEST.json`, which records the package version, full source commit, supported Runtime Protocol versions, package IDs, asset sizes, and frozen SHA-256 hashes. `SHA256SUMS.txt` covers every release payload including that manifest; the checksum index itself is the detached verifier. The `.nupkg` repository metadata carries the same source commit. NuGet.org adds its repository signature after upload, so the publisher verifies the signed package's content hash against the frozen unsigned Release asset instead of comparing the outer ZIP bytes.

## Coordinates and lifecycle

Every event has a monotonically increasing `sequence`, stable `eventId`, `(sessionId, actorId, inputId)`, and optional `runId`, `turn`/`turnId`, and `itemId`/`itemKind`. Runs, turns, and items publish `started`, `delta`, and `completed` lifecycles. Message, tool, durable action, approval, interaction, artifact, delegation, plan, media, and status items share this envelope.

`GameRuntimeReducer` rejects mixed sessions, non-contiguous sequences, duplicate starts, completions without starts, and changes of run identity. A terminal run reconciles any still-open presentation item with an explicit `item_interrupted` before the terminal result. It never invents or repeats a durable game action.

## Server endpoints

| Endpoint | Purpose |
| --- | --- |
| `POST /runtime/v1/initialize` | Negotiate protocol version and capabilities |
| `POST /runtime/v1/run/stream` | Start or reconnect to one idempotently identified input and receive SSE |
| `POST /runtime/v1/events` | Read a bounded cursor page without opening a stream |
| `POST /runtime/v1/control/steer` | Steer only the exact active run and turn |
| `POST /runtime/v1/control/interrupt` | Interrupt and wait for the exact run to settle |

The same identity-derived owner authorization and host-controlled audience projection used by the v1 server run, usage, transcript, approval, and durable action endpoints applies before Runtime state or runtime state is touched. A bounded body credential is accepted for local engine clients that cannot set headers; the host maps it to a principal, and ownership is still derived from that principal. Credentials are never stored in a transcript, event, journal, exception, or result.

The public C# client is `GameRuntimeServerClient`:

```csharp
var client = new GameRuntimeServerClient(new GameRuntimeServerClientOptions(
    httpClient,
    new Uri("http://127.0.0.1:5157/")));

var negotiated = await client.InitializeAsync();
var cursor = await client.StreamAsync(
    input,
    requestId: "host-command-42",
    (value, cancellationToken) =>
    {
        engineQueue.Enqueue(value); // deliver on the engine's main thread later
        return default;
    });
```

The same `(session, actor, input, requestId, inputJson)` identifies a reconnect. Reusing an input with different request content fails closed.

## Reconnect and reconciliation

SSE responses set `id:` to the canonical event ID. Persist the last fully applied ID and reconnect with `Last-Event-ID`. The server keeps running if one HTTP caller disconnects. It replays retained events without starting a second model run.

Retention is bounded. An unknown or expired cursor produces a `gap` event and `requiresTranscriptReconciliation=true`. Stop reducing incremental items, read the authorized durable transcript through `ServerGameAgentClient.ReadTranscriptAsync`, rebuild the presentation state, and then continue from the page's `nextAfterSequence`. That cursor represents the last scanned event and can be greater than the last visible event when audience projection removed private data.

A reconnect cannot authorize a repeated game mutation. Non-idempotent game tools still pass through `DurableGameActionDispatcher`, `operationId`, journal, authoritative receipt, and reconciliation. Runtime only replays observations of that lifecycle.

## Exact control

Read the current `runId` and `turn` from the stream, then bind both into `GameRuntimeControlRequest`. A delayed request returns `runMismatch`, `turnMismatch`, `controlClosed`, or `idle`; it cannot steer or interrupt a newer run. Accepted interrupt does not report completion until the runtime lane has settled and emitted its terminal state.

Legacy uncoordinated `TrySteer`/`TryAbort` remains available for tightly coupled in-process code. Remote and delayed clients should always use exact coordinates.

## Compatibility rules

- Negotiate before using optional capabilities; never infer support from a server brand or version string.
- Adding an optional capability or additive payload field does not redefine an existing lifecycle.
- Changing required fields, enum meaning, cursor semantics, or lifecycle ordering requires a new protocol version.
- JSON readers reject duplicate properties, use a maximum depth of 128, and enforce the documented character/page limits.
- `payloadJson`, `inputJson`, and `messageJson` contain one bounded canonical JSON value; they preserve the existing runtime wire contracts without nesting provider-specific objects into the Runtime schema.
- Hidden reasoning, signatures, private messages, credentials, and private tool details are never part of a non-internal projection.

Run `dotnet test tests/OpenGameAgent.Runtime.Protocol.Tests -c Release` for fixture/schema checks and `dotnet test tests/OpenGameAgent.Server.Tests -c Release` for authorization, projection, replay, and exact-control conformance.
