# Unreal sidecar client

`OpenGameAgent` is the supported Unreal C++ integration. Unity 6, Godot, and Unreal consume the same Runtime Protocol,
while the TypeScript sidecar owns Pi, model access, memory, planning, and optional extensions. The game remains the only
authority that applies world changes.

On the currently gated Unreal 5.8 Win64 x64 target, the plugin uses the engine-bundled libcurl and Unreal's certificate manager. Redirect following, ambient proxy use, netrc credentials, and non-HTTP protocols are disabled. A target without this verified backend fails closed instead of falling back to a transport that could replay an authenticated POST across a redirect.

The host must configure the module's process-wide concurrent-request and queued-request budgets once through `FOpenGameAgentModule::ConfigureTransport` before constructing clients. Each client separately requires host-configured request, JSON response, attachment response, aggregate response-header, response-header count, access-token, URL, and total-timeout budgets. Repeating the same module configuration is idempotent; attempting to change it after the shared transport is created fails closed. The shared bounded worker pool and bounded GameThread handoff keep those process budgets effective from admission through completion delivery. The total timeout starts at admission and therefore includes queue wait.

All public client request methods must be called on the GameThread; both immediate failures and asynchronous completions therefore share one callback-thread contract. The header callback selects the success or error body budget before the body callback can store bytes; `Content-Length` is checked before the body, while unknown-length and chunked bodies remain incrementally bounded. Cancelling before GameThread delivery begins synchronously detaches and suppresses the user callback. Once delivery has begun, cancellation does not retract a callback already executing. The module stops admission, drains transport work and queued completion destruction, and then releases its static libcurl process state; dynamic module reload is disabled.

`ReadAttachment` verifies the immutable reference, PNG media, length, strong ETag, privacy and nosniff policy, SHA-256 identity, and RFC 9530 digest before returning owned bytes.

`ReadDeferred`, `ResumeDeferred`, and `AbandonDeferred` expose the same Runtime deferred-response lifecycle on the GameThread. Every invocation makes one authenticated request through the existing bounded transport; the client does not poll, retry, coalesce, or infer command completion. Cancelling an `IOpenGameAgentRequest` suppresses its local callback when delivery has not begun, but it does not abandon provider work. After an uncertain response, reconcile locally with `ReadDeferred` before issuing another explicit command. If the host also consumes the canonical Runtime event stream through an external process or SDK, reconcile that stream as an additional source of durable evidence; this Unreal package does not expose the event stream. Call `AbandonDeferred` only when provider abandonment is intended.

Create an `IOpenGameAgentAuthoritativeActionAdapter`, configure `FOpenGameAgentActionPumpOptions`, and keep the returned shared pump alive. Execution duration, idle polling, and the finite receipt/reconcile retry-delay schedule are required host policy; the plugin supplies no operational defaults. Call `Tick` from the GameThread and `Stop` during teardown.

The pump consumes the Runtime's atomic next-delivery contract one action at a time. A `dispatch` delivery calls `Execute`; a `reconcile` delivery calls only `Inspect`. Execution receives a cooperative context whose `TryCommit` checks the local epoch, stop/deadline, GameThread, and the adapter's `RecheckExpectedAuthority` result immediately before running one synchronous game-owned world transaction. That transaction receives the complete action and owns operation deduplication, world mutation, and resulting evidence. Duplicate, cancelled, expired, and late completions cannot open a new write. Receipt and reconciliation requests retain the delivery's exact claim token and attempt. Only no-response/ambiguous transport failures and 5xx responses use the host retry schedule; 3xx and other 4xx responses halt. A 409 first reads Runtime authority: a current unresolved fence can be inspected, but a second unchanged unresolved fence halts instead of re-inspecting forever. An outcome that remains unknown halts without admitting a later action.

The module uses the Unreal 5.8-supported C++20 level and Unreal JSON types, so the plugin does not bundle a second JSON
runtime or expose a separate engine-neutral C++ client.

`npm run unreal:structure:check` verifies the portable thin-plugin structure. The host-required command
`npm run unreal:check -- --engine-root=<path> --timeout-ms=<host-budget>` packages the plugin with Unreal Build Tool in
Editor, Development Game, and Shipping Game configurations, then runs validation and real transport automation in an
unattended editor. The explicit gate timeout bounds each external process, helper readiness and helper shutdown; the same
host budget is passed to the transport fixture. The transport gate uses isolated source and redirect-target listeners to
prove that a 307 never reaches the second listener, and verifies status-selected limits with both `Content-Length` and
chunked bodies. The current compatibility gate targets Unreal Engine 5.8 Win64 x64; other targets and older engine
versions are validated only when a consumer requires them.
