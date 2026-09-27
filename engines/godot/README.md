# OpenGameAgent Godot C# integration

`OpenGameAgentRuntimeNode` is the supported Godot integration. Its private C#/.NET 8 transport substrate lives under
`csharp/` so the complete integration can be copied and built without a repository-external SDK. The node receives atomic
dispatch-or-reconcile deliveries in a host-sized bounded channel. `IGodotAuthoritativeActionAdapter.Execute` and
`Inspect` are invoked only from `_Process`, preserving Godot main-thread ownership of game state. A reconcile delivery
calls `Inspect` and never `Execute`.

Execution receives a monotonic local epoch and cooperative cancellation token. A game write must use
`GodotActionExecutionContext.TryCommit`; the node rechecks the active epoch, effective execution deadline, Godot main
thread, and `RecheckExpectedAuthority` immediately before running the supplied synchronous game transaction. The
permit cannot cross an await or frame. Completion after a deadline, duplicate completion, and completion after the node
stops are rejected. The node stamps receipts and reconciliations with the exact operation ID, claim token, and attempt
from the active delivery rather than accepting those fields from the adapter.
Once the supplied game transaction starts, `TryCommit` reports that accepted world write even if the adapter returns an
invalid null completion; that post-write ambiguity is queued for authoritative `Inspect` and reconciliation rather than
being exposed as a reusable rejected permit.

Adapter-owned completion and inspection evidence is captured once with the shared protocol JSON semantics at the
completion acceptance point or when `Inspect` returns. Nested result data and authority scalars therefore cannot change
an accepted request between finite delivery retries; the delivered action itself is not deep-copied. A 2xx response for
an `Unknown` completion or reconciliation halts the channel because transport acceptance does not resolve the action.

Execution duration and the finite receipt/reconcile retry-delay schedule are required host policy. The first delivery
attempt is immediate. Only ambiguous transport failures and 5xx responses use the remaining supplied delays; 4xx
responses are not retried, and a conflict triggers an authoritative action read before main-thread inspection. An
unchanged unresolved claim fence after a prior authoritative read halts instead of repeatedly inspecting; a changed
claim fence may be inspected once. An exhausted ambiguous delivery halts that action channel instead of claiming
another action. No outbox, rollback, Agent loop, workflow state, or retry defaults live in the engine adapter.

`ReadAttachmentAsync` uses a separate host-supplied attachment byte budget and returns PNG bytes only after the Godot
C# transport verifies the response length, SHA-256 content address, RFC 9530 digest, ETag, media type, and privacy
headers. The transport owns a `SocketsHttpHandler`, forces automatic redirects off after host transport configuration,
and admits HTTPS or exact loopback HTTP endpoints only.

`ReadDeferredAsync`, `ResumeDeferredAsync`, and `AbandonDeferredAsync` bind the node's configured session and actor to
the Godot C# transport. They perform one explicit lifecycle request and never poll or retry automatically;
after an uncertain response, read the pending state and canonical events before deciding whether to issue another
command.

All request/response bytes, queue capacity, execution duration, stream reconnect policy, and delivery retry delays are
required host configuration. `MaxRequestBytes` is enforced on serialized JSON before dispatch, including the action SSE
POST. The OpenGameAgent sidecar or remote server owns the Pi-backed Agent loop.

`npm run godot:check` stages the complete `engines/godot` tree in isolation, runs the C# transport conformance executable,
builds both Godot projects, imports the project, and executes the action-pump scene in the configured Godot editor. The
equivalent focused editor commands inside a copied integration are:

```powershell
dotnet build engines/godot/OpenGameAgentGodot/OpenGameAgentGodot.csproj --configuration Debug
dotnet build engines/godot/OpenGameAgentGodot.Conformance/OpenGameAgentGodot.Conformance.csproj --configuration Debug
godot_console.exe --headless --path engines/godot/OpenGameAgentGodot.Conformance --scene res://action_pump_conformance.tscn
```
