# Structured Tool Loop

1. Import this sample from Package Manager.
2. Add `StructuredToolLoopSample` to an active GameObject.
3. Enter Play Mode.

The sample builds an `IDurableAgentRuntime`, supplies structured JSON context in
a `DurableRunRequest`, receives a streamed tool call, executes `gather_food`
through the Unity main-thread host, journals the request and receipt, and logs
the final structured JSON.

The bundled streaming provider is deterministic and makes no network request.
The sample persists its journal with `FileSessionStore` under
`Application.persistentDataPath`. Replace the provider and store at the
composition root for a game integration.
