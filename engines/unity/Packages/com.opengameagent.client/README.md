# OpenGameAgent Client for Unity

This package connects Unity 2022.3 or newer to a separately hosted OpenGameAgent runtime. Unity remains authoritative for game rules and world writes. The runtime owns model calls, Agent turns, memory, planning, realtime orchestration, and durable action delivery.

The client exposes raw bounded JSON and SSE contracts so new runtime capabilities do not require a second Agent implementation inside Unity. Remote services require HTTPS; plaintext HTTP is limited to loopback. Authentication is added beside the input and is never inserted into the model-visible payload.

Use `OpenGameAgentClient.RunAsync` for streamed runs, exact run coordinates for steer/follow-up/abort, and `StreamActionsAsync` plus receipt/reconcile endpoints for authoritative game actions. Cancelling a local request does not claim that a previously dispatched durable action was rolled back.
