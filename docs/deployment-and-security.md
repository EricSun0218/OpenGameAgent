# Deployment and security

OpenGameAgent supports in-process and server placement. Choose based on authority, secrets, latency, offline requirements, and operational cost—not engine branding.

## In the engine

Use the local runtime when:

- the game is single-player or peer-authoritative;
- the player supplies a model endpoint/key;
- the model endpoint is local;
- direct game-context access and minimum latency matter more than central control.

The model request does not block the engine frame when awaited correctly, but action handlers must marshal engine mutations to the main thread. A permanent provider key included in a shipped executable, resource, environment file, or managed assembly can be extracted. Running inside Unity or Godot does not protect it.

For a BYOK game, store the player's key using the platform credential facilities selected by the game and resolve it at request time. For developer-funded inference, point the client at a developer-controlled gateway. `DeveloperGatewayProvider` can exchange game authentication for a short-lived scoped credential and cache it only until its refresh window; the permanent upstream key remains on the developer's infrastructure. The gateway still needs account authorization, quotas, revocation, abuse controls, and TLS.

## In an existing game server

If the game has an authoritative C# server, reference `OpenGameAgent` there directly. This keeps rules, state transactions, operation recovery, and agent execution close together. Engine clients send normal game commands; they do not need to know that an agent produced a decision.

## Separate agent service

Use `OpenGameAgent.Server` when inference credentials, scaling, or agent updates must be centralized. Configure with environment variables or another ASP.NET Core configuration source:

```text
OpenGameAgent__ModelEndpoint=https://provider.example/v1/chat/completions
OpenGameAgent__Model=your-model
OpenGameAgent__ApiKey=provider-secret
OpenGameAgent__ServerApiKey=game-to-agent-secret
OpenGameAgent__DataDirectory=/var/lib/opengameagent/sessions
```

The included service exposes:

- `GET /healthz`
- `GET /v1/capabilities`
- `POST /v1/run`
- `POST /v1/run/stream` (Server-Sent Events)
- `POST /v1/control/steer`
- `POST /v1/control/abort`

Mutation endpoints require a JSON content type, parse with a fixed depth limit, and reject request bodies larger than 8 MB by default. `MapOpenGameAgent` accepts a lower deployment-specific body limit; the reverse proxy should enforce an equal or tighter limit before buffering requests.

When `ServerApiKey` is set, run and control endpoints require `Authorization: Bearer <key>`. The middleware supplies the stable authenticated subject `server-api-key` unless an upstream authentication system already supplied a principal. If the key is omitted, those endpoints are unauthenticated; only do that behind an already authenticated trusted boundary. Health and capability endpoints remain public.

Register an `IGameAgentOwnerAuthorizer` for player-facing or multi-tenant deployments. Every run, stream, steer, and abort request is then authorized against the authenticated principal and the parsed `(session, actor)` resource before the runtime, session store, or active actor is touched. Anonymous requests receive `401`; authenticated principals that do not own the resource receive `403`. The same operation contract reserves usage and durable-action operations so those endpoints use the identical ownership decision. Derive ownership from authenticated claims or an authoritative host store—never from an owner field supplied in the request payload. Without a registered authorizer the endpoint retains its legacy single-owner behavior for compatible trusted deployments.

Control requests only address an already active `(session, actor)` loop; they cannot register tools or mutate game state directly. Put TLS, request-rate limits, tenant quotas, and abuse protection at the gateway. The included shared-secret gate identifies one deployment-wide subject; it is not a multi-user account system.

### Output audiences

Register an `IGameAgentAudiencePolicy` when server output can be observed by more than one trust class. The policy resolves a viewer from the authenticated principal and classifies every event or message as `Internal`, `Owner`, `Public`, or a named `Recipient`. The framework—not the model response or tool payload—applies that decision to both JSON and SSE output. Non-internal viewers never receive reasoning text or signatures, redacted reasoning, tool arguments, tool results, tool progress details, or message metadata. An internal viewer can receive the complete diagnostic stream.

`MetadataGameAgentAudiencePolicy` is the safe stock policy for persisted annotations. `GameAgentAudienceMetadata.WithAudience` accepts only host-authored assistant or custom messages; user messages and tool results cannot promote themselves with request metadata. Audience and recipient annotations use the existing bounded message metadata and survive memory and file-session round trips. Redacted reasoning state is also preserved by the file-session format. Hosts that compute audience from an external ACL may implement the policy directly instead.

The included file stores coordinate local writers through cross-process leases when they use the same data directory. They are not distributed storage. Multi-host services must replace the interfaces with transactional shared storage and coordinate actor ownership. Custom session, workflow, action, artifact, delegation, and ranking implementations are checked at their trust boundaries; inconsistent saved state and cross-session data are rejected.

## Remote game actions

The included server runs tools that are registered in its process. If authoritative game state lives elsewhere, prefer one of these designs:

1. run the runtime inside the authoritative game server;
2. implement server-side tools that call authenticated internal game APIs using operation IDs;
3. let the agent service return a proposal and have the game execute it as a separate command.

Do not create an unauthenticated generic “execute any client action” endpoint. Tool catalogs and permissions are part of the game deployment.

## Untrusted boundaries

Treat all of the following as untrusted or potentially sensitive:

- model output and tool arguments;
- imported skill instructions;
- player-authored prompts and structured payloads;
- remote resources and generated-media URLs;
- external tool-server descriptions, schemas, and results;
- provider errors and streamed event sizes;
- stored transcripts, memory, and game context.

Always expose narrow tools with JSON Schema, revalidate in game code, and enforce permissions independently of prompts. Do not expose arbitrary shell, code execution, filesystem, network proxy, reflection, or unrestricted asset-write tools to game content.

The external-tool connector defaults to one on-demand search/describe/call tool, which avoids eagerly placing every remote schema into the model context and does not connect during prompt assembly. Remote arguments are schema-validated locally before execution. Treat access to that proxy as access to every server behind it: place `ToolPolicyExtension` or equivalent game authorization in front of calls and expose only trusted servers. Use HTTPS for HTTP transport unless a developer explicitly opts into an insecure development endpoint.

## Data and retention

The local stores are not encrypted. Put them in an access-controlled game save or service data directory. Decide which prompts, context, memories, artifacts, delegation records, generated assets, and provider identifiers may contain player data. Implement retention, export, deletion, consent, and regional handling for your product. The included stores retain completed records needed for deduplication and recovery and do not provide a generic purge policy; archive them only when the game can prove their replay-safety window has ended.

Never log credentials. Avoid logging full prompts and tool payloads in production unless the player has consented and access is controlled.

Use provider endpoints without URI-embedded credentials. If an `HttpClient` follows redirects, configure its handler so authentication and sensitive custom headers cannot be forwarded to an untrusted origin; prefer fixed provider endpoints and deny unexpected redirects.

## Limits and cancellation

Keep runtime limits below the maximum values accepted by the framework. Set tighter limits for user-authored content, including provider response characters and tool calls per response. A canceled or timed-out write may have committed: reconcile by operation ID. Read-only work may be retried; non-idempotent writes must not be retried blindly.

Use a new session/save namespace when a forked save can coexist with its source. A new `TimelineId` separates game-time ordering, but transcripts and extension state are keyed by session and actor. Workflow checkpoints are not automatically atomic with game-state commits; side-effecting nodes should dispatch through stable operation IDs.

See [SECURITY.md](../SECURITY.md) for vulnerability reporting.
