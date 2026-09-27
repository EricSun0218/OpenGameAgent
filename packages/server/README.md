# @opengameagent/server

Optional self-hosted HTTP and server-sent-event transport for OpenGameAgent. The host supplies authentication, actor authorization, and every resource bound.

`POST /runtime/v1/runs` applies the HTTP request limits separately from Runtime payload admission limits:

| Limit | HTTP status | Error code |
| --- | --- | --- |
| Complete request exceeds `maxRequestBytes` | 413 | `body_too_large` |
| Request nesting exceeds `maxJsonDepth` | 400 | `json_too_deep` |
| Canonical UTF-8 payload exceeds Runtime `maxInputBytes` | 413 | `input_too_large` |
| Payload value depth exceeds Runtime `maxInputDepth` | 400 | `input_too_deep` |

The Runtime input errors include the failed configuration field and its limit in `error.message`. They reject before
creating a run, reserving queue capacity, persisting events, or calling a provider. Correct the payload before submitting
again; the rejected input ID remains available. Internal storage and other JSON/range failures still return a sanitized
`500 internal_error` rather than being attributed to the input.
Error responses still obey `maxResponseBytes`; when that budget cannot represent a diagnostic, the existing bounded
`500 response_too_large` response takes precedence.

After `GameRuntime.stopAdmission()`, new run submissions and deferred resumes return `503 runtime_stopping`; read,
control, reconciliation, and explicit deferred-abandonment paths remain available for shutdown. The host still owns the
Node listener. It must close ordinary requests and active SSE connections before closing the Runtime's shared storage.

When an optional health service is configured, `GET /runtime/v1/health` returns its bounded dependency snapshot after the same Runtime authentication used by other routes. The response is `private, no-store`. It is advertised separately as `health.snapshot`; `/runtime/v1/initialize` remains static capability negotiation and is not readiness evidence. The shared listener never exposes detailed health without authentication.

配置可选 health service 后，`GET /runtime/v1/health` 会先执行与其他 Runtime 路由相同的身份认证，再返回有界依赖快照，响应固定为 `private, no-store`。该能力独立声明为 `health.snapshot`；`/runtime/v1/initialize` 仍然只负责静态能力协商，不能作为就绪证据。共享监听器绝不会匿名暴露详细健康信息。

Event SSE backlog overflow and over-budget frames close the connection without advancing past the last delivered
durable revision. A client can reconnect from that revision and replay retained events; while retained, an over-budget
event remains pending until the host supplies a limit that can represent it.

The action-stream poll interval is mandatory and may not exceed Node's `2,147,483,647 ms` timer ceiling. The action
claim lease is game-authority time rather than a JavaScript timer and is not assigned that unrelated maximum.
`POST /runtime/v1/actions/recovery` is the process-restart-safe, actor-scoped recovery read: it returns only the oldest
already claimed or uncertain Action and never claims pending work or advances a lease. Hosts should drain this endpoint
before opening normal Action delivery after losing exact run identity.

The event and Action endpoints share `maxSseConnections`. When hosts use the official exact run-session clients, each
observed accepted non-terminal run holds one event connection and each started non-terminal run with Actions enabled
holds one additional Action connection. Configure
`maxSseConnections >= observedAcceptedNonTerminalSessions + startedActionSessions`; with at most `Q` observed queued and
`A` active Action-enabled runs, this is `Q + 2 * A`. Add any direct non-session streams separately. Approval discovery
uses ordinary bounded HTTP requests, not SSE.

Request JSON is admitted before runtime access with explicit byte/depth limits, duplicate-key rejection, and rejection of integer values outside JavaScript's exact safe-integer range. Non-integer JSON numbers use finite IEEE-754 semantics; exact protocol counters and game time use canonical decimal strings.

`POST /runtime/v1/authority/close` and `/runtime/v1/authority/read` require actor-owner access and are advertised as
`authority.close`. A close request identifies `{ sessionId, actorId, scope, generation, operationId }`; an identical
replay returns the original closure. Closed scoped submissions fail with `409 authority_closed`, and conflicting close
operations fail with `409 authority_conflict`. Run requests carry optional `admissionAuthority: { scope, generation }`.
Other generations and runs without an admission authority remain independent on the same actor stream.

An optional `goalRetirement` additionally requires `access.authorizeGoalRetirement(principal, access, request, signal)`
to return `true`. The server passes the complete validated, immutable close request to this callback. Hosts must
authorize its exact Goal partition and retirement metadata for the principal; actor ownership alone does not grant
permission to retire an arbitrary Goal partition. The default is denial. This permits application-specific owner and
timeline mappings without weakening the separate Goal authorization boundary. Configure the event store and Goal store
to share the same authority transaction when enabling this operation.

An optional `workRetirement` separately requires `access.authorizeWorkRetirement(principal, access, request, signal)`
to return `true` for the complete immutable request. Actor ownership or Goal approval alone does not grant this
permission. The exact five-field work lane must match the request's session and actor; the host authorizes its owner,
timeline and generation mapping. Configure the Runtime's explicit `drainWorkAuthority` callback and its same-authority
Schedule/Mailbox store bindings. Reads may report `workRetirementResult.phase: "fenced"` while drain is unfinished;
only `"finalized"` acknowledges final lease retirement. An uncertain close is resumed with the identical complete request.

The deferred lifecycle endpoints expose only the operation identifier, input identifier, and run coordinate. Reads are
observational. Resume returns run admission for one explicit provider fetch, while abandonment commits the local aborted
state before reporting best-effort provider cancellation. Authentication and actor authorization precede all three
operations; provider handles and original game input are never returned.

The optional attachment service exposes verified local PNG objects only after both actor authorization and a separate
game-supplied attachment authorization callback. The host supplies a platform-supported response byte limit; the server
rechecks the byte length and SHA-256 content address before returning a private, non-cacheable binary response.
