# Godot C# transport substrate

This directory is an internal implementation part of the Godot integration, not a separately supported engine or a
general-purpose .NET SDK. It contains the generated C# protocol DTOs and the bounded .NET 8 HTTP/SSE transport used by
`OpenGameAgentRuntimeNode`. The OpenGameAgent sidecar owns the Pi-backed Agent loop and all authoritative game writes
remain with the game.

`ReadDeferredAsync`, `ResumeDeferredAsync`, and `AbandonDeferredAsync` each perform one explicit request. They do not poll, coalesce, or retry automatically. Cancelling the local `CancellationToken` stops the client wait but does not prove whether the server committed the command. After an uncertain response, read the pending deferred state and canonical events before deciding whether to issue another explicit command.

`RuntimeRunRequest.AdmissionAuthority` optionally binds a run to an exact scope and decimal generation. Submit and run
session methods capture it with the payload before authentication or transport; sessions validate the queued authority.
`CloseAuthorityAsync` and `ReadAuthorityClosureAsync` snapshot their requests and validate the returned durable closure
against the exact key and, for close, complete operation. Replay an uncertain close using the original request and
operation ID. Closing one generation leaves other generations and unscoped runs on the same actor stream available.
The Godot node exposes these methods for its configured actor stream. Unity exposes corresponding coroutine methods
and captures JSON request bodies when the method is called, before the coroutine starts.

Request, HTTP response, attachment response, and SSE event byte limits are required host configuration. JSON bodies are
serialized and checked against `MaxRequestBytes` before the HTTP transport is called, including the POST body used to
open an action stream. The client accepts HTTPS or exact loopback HTTP, rejects redirects, and forwards caller
cancellation through authentication, transport, and bounded response decoding.

`NextActionAsync` and `StreamActionsAsync` consume the same atomic dispatch-or-reconcile contract. Receipt and
reconciliation requests carry the delivery's operation ID, claim token, and attempt; the client sends each explicit
request once and leaves game inspection, execution, delivery policy, and authoritative writes to the host adapter.
After a host restart, `RecoverActorActionsAsync` is the supported actor-scoped recovery loop. The host supplies a
positive maximum Action count, a callback deadline, and a read-only game reconciliation callback. The client rejects
foreign or malformed records before that callback, submits the exact claim fences to the reconcile-only endpoint, and
validates the returned authoritative record. It never dispatches or executes recovered work. `unknown` remains explicit
and throws `RuntimeActorActionRecoveryUnresolvedException`; the host must resolve that operation before normal Action
consumption. `RecoverActionAsync` remains the single-probe primitive used by that loop.

Generated approval list and decision DTOs expose the optional host-projected JSON as `Request.Presentation`. A Godot
host renders that application-owned value while retaining the approval ID, revision, and request fingerprint returned
with the same record; presentation data never grants Action authority.

`RuntimeClient.StartRunSessionAsync()` is the supported high-level lifecycle for one accepted run. It starts event
replay at the returned admission cursor, validates the exact `run.queued` identity, scopes Action and Approval workers to
that run, and resolves only from its matching terminal event. It does not open Action or Approval consumption before
the matching validated `run.started`, so queued sessions use only event SSE. `RuntimeRunApprovalOptions.WaitDuration`
is the host-selected bound for one cursor-based server wait. Approval discovery runs immediately after activation and
then keeps at most one wait request open; unchanged state produces no client polling churn. A successful local decision
cancels that wait and immediately reconciles from its last validated cursor. The session validates complete Action
identity and dispatch/reconcile state plus complete Approval
request/record identity before invoking the game. Event, Action-delivery, and pending-Approval callbacks all require
explicit `TimeSpan` deadlines. `Close()` stops
local observation only; `AbortAsync()` sends one exact remote abort and terminal truth still comes from `Completion`.

Persist both the exact `RuntimeRunRequest` and returned `RuntimeRunAccepted` before treating submission as recoverable.
After restart, `AttachRunSessionAsync()` validates those identities, optionally drains the configured actor recovery,
and observes from the persisted admission cursor without posting `/runtime/v1/runs` again. The attached run must be the
actor's admitted nonterminal run; once recovery reports empty, its ordinary run-scoped Action worker may resume pending
work. Supplying `Recovery` to a new session applies the same actor recovery boundary before submission.

For concurrent sessions, configure the sidecar with
`MaxSseConnections >= observedAcceptedNonTerminalSessions + startedActionSessions`. If every observed run enables
Actions and the host observes at most `Q` queued plus `A` active runs, the bound is `Q + 2 * A`; add direct non-session
streams separately.
