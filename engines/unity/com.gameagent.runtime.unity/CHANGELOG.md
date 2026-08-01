# Changelog

## [0.1.0-alpha.1] - 2026-07-28

- Added routed Direct/Agent/Workflow execution, stateless completion, and
  per-operation model/provider controls through the built runtime backend.
- Added bounded child Agent execution and cancellation with durable lineage.
- Added the first Unity UPM host package.
- Added bounded main-thread dispatch and Action handler marshalling.
- Added MonoBehaviour lifecycle, cancellation, and durable shutdown.
- Added injectable durable runtime backends with run, resume, and control-plane
  access.
- Bundled the durable composition builder, workflow module, and both streaming
  provider adapters in assembled UPM artifacts.
- Added Unity-safe structured DTO bridges.
- Added sample, EditMode/PlayMode tests, stub conformance, and Player
  build-and-execution gates with durable pass markers for Mono and IL2CPP.
- Added a reserved terminal-observer queue that cannot be displaced by
  main-thread action traffic and rejects new work before a terminal event could
  become unobservable. Controlled shutdown waits for every reservation already
  issued to publish or be abandoned; published observers survive shutdown and
  can still be pumped on the main thread with a between-callback frame budget.
- Added `RunFaultedDetailed` with operation kind, stable run lineage, and a
  conservative reconciliation requirement while retaining `RunFaulted` for
  compatibility. Throwing completion and fault subscribers are isolated
  individually across all terminal event shapes.
- Pending ordinary run cancellation is promoted through the reserved shutdown
  lane during teardown, with exactly-once token cancellation and full dispatch
  drain before per-run resources are released.
- Mutable custom-backend requests are structurally bounded before serialization
  and snapshotted after admission; snapshot failure returns cancellation
  ownership and custom backends retain semantic validation authority.
- Runtime-event and application-pause subscribers are isolated per subscriber,
  matching the existing terminal-observer behavior.
