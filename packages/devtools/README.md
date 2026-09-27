# @opengameagent/devtools

Optional, content-safe observability and conformance tools for OpenGameAgent hosts.

- emits a fixed trace vocabulary without prompts, model output, tool arguments, receipts, credentials, hidden reasoning, or raw identifiers;
- adapts safe records to the OpenTelemetry API and exposes the official Node SDK for host-selected exporters;
- encodes and validates deterministic bounded JSONL for local replay and evaluation;
- aggregates timings and evaluates only explicit host thresholds;
- registers auditable protocol, reference, derived, and host-required limits;
- supplies deterministic Pi provider/Tool fixtures and fault checkpoints.

`ExactToolRepeatMetricsProjector` derives advisory and termination totals from canonical
`tool.repeat.detected` events and returns them as canonical decimals, avoiding a process-lifetime JavaScript counter
ceiling. `OpenTelemetryExactToolRepeatMetricSink` exports one increment per detection and only the fixed
`advisory`/`terminated` action attribute. It does not export Tool names, call identifiers, arguments, results, or model
content.

`SafeTraceRecorder` requires the host to choose a sink failure policy. `fail-fast` stops at the first sink failure;
`attempt-all` invokes every sink in order and reports the collected failures in an `AggregateError`. Neither policy makes
multi-sink delivery atomic. A successful record must use `outcome: "ok"` with `errorKind: "none"`; every other outcome
must use a non-`none` error kind. Invalid combinations are rejected before fanout.

The OpenTelemetry adapter leaves span status unset for successful and caller-cancelled operations and omits
`error.type`. Error, rejected, and uncertain outcomes set span status to `ERROR` and reuse the fixed `errorKind`
vocabulary as the low-cardinality `error.type`. Domain state such as dependency health remains domain data and does not
change the status of an otherwise successful span.

`ModelContextProvenanceRecorder` provides an optional append-only local manifest for the Agent provenance sink. Its
schema is a strict allowlist: every persisted identity must be a deterministic `pv_<sha256>` alias, while request and
response content, thinking, Tool definitions, Tool calls/results, credentials, raw provider or host identifiers, handles,
errors, paths, URLs, accounts, and arbitrary extension fields are rejected rather than captured and later redacted. The
Agent produces these aliases before the sink sees a record, and image media types must be one of the protocol's four
allowlisted values. A request records both the original requested route and one numbered target attempt; its response
records the served-model alias and terminal classification. Attempt numbers start at one, increase without gaps for the
same request coordinate, and cannot advance until the preceding attempt is settled. The served model may differ from the
target model when the provider performs server-side routing. Unsettled request records are preserved after a crash or
settlement failure. Exact-repeat advisory requests may additionally contain only the allowlisted immutable policy
lineage, aliased event and Tool identities, event revision, canonical count, and advisory content fingerprint needed to
reconstruct the transient provider context; raw Tool arguments and event identifiers remain excluded.

The recorder serializes each complete load/check/append transaction, so concurrent actors sharing one recorder do not
produce false conflicts. The in-memory store is useful for deterministic tests. The file store persists one bounded JSON
document with atomic replacement, continuous revisions, corruption checks, pre-read size admission, and same-process
serialization for the same resolved path. It is a single-logical-writer building block, not a lock service: independent
recorders or processes must use exclusive ownership, and a stale writer receives an explicit conflict without retry.
Its cached view is invalidated so a later explicit `restore()` can read the current head; the failed append itself is
never retried. A Store append acknowledgement must equal the complete candidate state the recorder supplied; a stale or
substituted but otherwise valid state is rejected and cannot authorize a provider request or settle one.
Record count, record bytes, total store bytes, and file document bytes are mandatory host configuration; the package
supplies no retention or rollover policy.

Delay faults accept zero through Node's `2,147,483,647 ms` timer ceiling; larger values are rejected when the fixture
is constructed instead of being silently scheduled as a one-millisecond delay.

The TypeScript Agent runtime does not require this package. Importing the OpenTelemetry API remains a no-op until the host starts an SDK. Export destination, batching, sampling, and retention remain host configuration. Provenance context
fingerprints and aliases are local correlation and integrity material, are not anonymization, and are not projected to
OpenTelemetry.
