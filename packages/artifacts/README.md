# `@opengameagent/artifacts`

Optional immutable, session-and-actor-isolated text artifacts for the Pi-backed game Agent.

`GameArtifactService` is a request source that replaces an oversized all-text Pi Tool result with a bounded reference
only after the full text is durably stored. The paired read Tool returns bounded UTF-8 byte pages. Mixed media is
intentionally left to the media/attachment extensions rather than being copied into this text store.

The game supplies every limit, the canonical creation clock and the durable store. Storage failure after a Tool has
already completed does not turn that Tool into a retryable failure: the model receives a bounded `output_unavailable`
result with `doNotRetry: true`. Artifact reads fail closed to the same `not_found` result across missing and wrong-actor
lookups. Hosts own retention and may call the bounded, partition-scoped purge API explicitly.

`GameKnowledgeService` adds one parallel Pi Tool for querying host-registered external knowledge sources. The model chooses
only a registered source identifier, a strict-JSON query, and an explicit item count. The source receives the bound game
input so an in-process host adapter can authorize and project context, but no input or context crosses a transport unless
the host supplies that projection. Every returned item is normalized as untrusted external data under explicit count,
depth, and byte limits. Knowledge returns the complete normalized Tool result; when Artifact is also registered, the one
shared result transformer stores large text. Knowledge source, timeout, validation, and size failures are non-retryable;
Artifact storage failures use the generic completed-output-unavailable result.

`JsonHttpKnowledgeSource` is an optional fixed-endpoint adapter. It permits HTTPS and exact loopback HTTP, blocks redirects,
credentials in URLs, transport-controlled headers, non-JSON or non-UTF-8 responses, loose JSON, and declared or streamed
response overflow. It sends only `{ query, maxItems }` plus optional host-projected context. The host must configure all
byte, count, depth, concurrency, and timeout limits.

Register artifacts before knowledge so the Artifact result transformer can project every later Tool through the same
Pi result-finalization boundary:

```ts
const agent = new GameAgent({
  // model, modelStream, emitEvent, policy, and limits omitted
  toolSource: () => gameTools,
  requestSources: [artifacts, knowledge],
});
```

Each request source declares the final whole-result admission required by its own Tools. Artifact separately declares the
aggregate UTF-8 text it may inspect before replacing oversized content. `GameAgent` derives the complete multi-part
content envelope from that value and the host part limit, while retained Pi details and status fields independently stay
inside the ordinary final budget. It validates both stages before provider publication. Hosts should
align Knowledge `maxResultBytes` with Artifact `maxArtifactBytes` when every accepted Knowledge result must remain readable
after offload.
