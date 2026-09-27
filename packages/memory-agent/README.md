# @opengameagent/memory-agent

Optional bridges from `GameMemory` recall to the Pi-backed Agent. `MemoryContextSource` lets a host inject recalled records
into per-request context. `MemoryToolSource` contributes one read-only `memory_recall` Tool so the same Agent loop can decide
when to retrieve. The host binds owner, actor or session identity scope, timeline, generation, viewer, result limit and
vector failure policy for each request; only the Tool's natural-language query is model-controlled.

Both sources use the same fail-closed identity projection and omit persistence keys, search text, audience, owner, actor,
session, ranking scores and recall stage metrics from model-visible results. `MemoryContextSource.observeRecall` can
receive one frozen, content-free `{ vectorStatus, stages }` observation after a successful recall and projection; it is
not awaited, and observer failure cannot fail context assembly. Stage metrics also remain available through direct
`GameMemory.recall()`. Embedding providers remain optional, and this package does not add memory write Tools, another
Agent loop or an MCP transport.

Both bridges contribute validated stage durations to the active Agent's canonical `run.metrics` event without requiring
a host observer or host stopwatch. `durations.memoryPrepare` sums `partition` and `index-restore`; `memorySearch` sums
lexical, query embedding, vector, fusion, hydration, and rerank stages. Values are decimal microsecond strings. An empty
lane records zero search duration. Background work that the current recall did not await is not charged to that run.
The callback observation copies only the defined metric fields, so extra adapter fields cannot expose recall content.

`MemoryToolSource` preserves the host-visible recall status in its Tool result: `not-configured`, `unprepared`,
`preparing`, `preparation-failed`, `proven-empty`, `used`, or `failed-open`. These values let the host distinguish
background index lifecycle from provider failure without exposing it in model context. `MemoryContextSource` continues to
project only bounded memory content and does not expose retrieval status or stage metrics to the model.
