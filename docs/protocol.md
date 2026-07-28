# Wire protocol

The current protocol and schema version is `0.2`.

Canonical JSON Schemas live in [`schemas`](../schemas), with positive and
negative conformance fixtures in [`fixtures`](../fixtures).

## Main objects

| Object | Purpose |
| --- | --- |
| `ObservationEnvelope` | Typed input from the game, player, clock, sensor, or service |
| `AgentRun` | Durable lifecycle, budget, usage, and pending operation state |
| `ToolDescriptor` | Model-visible contract plus effect and thread metadata |
| `ToolInvocation` | Validated model request with resolved conflict keys |
| `ActionRequest` | Write-ahead command sent to game code |
| `ActionReceipt` | Authoritative game result |
| `SkillManifest` | Versioned instructions, tools, resources, and activation metadata |
| `TurnSnapshot` | Immutable prompt/tool/skill generation record |
| `RuntimeEvent` | Ordered durable or ephemeral trace event |

`AgentUsage.inputTokens`, `outputTokens`, and `costUsd` contain provider usage
that has been observed and durably charged. `hasUnaccountedUsage` and
`unaccountedProviderAttempts` make cancellation or transport failures explicit
when a dispatched provider request never supplies trustworthy final usage. In
that case the numeric totals are lower bounds, not an assertion that the missing
attempt cost zero. Each `provider.usage_uncertain` event preserves `providerId`,
`attemptId`, `streamAttemptId`, and `reasonCode` as structured fields for
provider-side reconciliation. Durable usage settles billing but does not settle
the response. A dispatch closes only with `provider.result_committed`,
`provider.result_discarded`, `provider.dispatch_known_zero`, or
`provider.usage_uncertain`. Recovery fails closed without another provider call
when either usage or the billed response is unresolved.

Every `provider.dispatch_started` event also records the actual route selected
for that attempt: `providerId`, `modelId`, `transportDialect`,
`providerCapabilityDigest`, and `providerRouteDigest`. Built-in providers expose
this metadata directly. A custom provider should implement
`IProviderRouteMetadataSource`; providers that omit it are recorded with the
explicit `unspecified` model and `custom.streaming.v1` dialect rather than an
inferred identity.

The runtime orders durable skill-system material before turn-specific messages
when constructing a provider request. `TurnSnapshot.stablePrefixHash` covers
only that semantic leading prefix plus the prompt-layout version, so it stays
unchanged while the prefix is unchanged. The `extensions.promptDigest` value
covers the complete turn transcript and effective tool/skill catalogs. This
separates cache-prefix identity from the dynamic request identity.

## Versioning

- `protocolVersion` identifies behavioral compatibility.
- `schemaVersion` identifies the object envelope schema.
- `contentSchemaVersion` identifies an observation payload owned by the game.
- Unknown extension data belongs under `extensions`.
- Runtime validators reject unsupported enum values and unsafe combinations.

For `ToolDescriptor`, `retryPolicy` is `never`, `safe_read`, or `idempotent`;
`idempotencyPolicy` is `required`, `best_effort`, or `none`; and `visibility`
is `direct`, `deferred`, or `internal`. `timeoutMs` is bounded to
1–86,400,000. These retry/idempotency fields are contract metadata in the
current alpha and do not by themselves schedule another host action.

Serialization entry points use generated metadata for the closed protocol type
set. The engine builds do not depend on runtime reflection.

## Input model

An observation carries exactly one of:

- inline JSON `payload`;
- a `resourceRef` with URI, media type, optional digest, and optional size.

The context compiler selects required items first, applies stable priority
ordering, removes expired items, and fails closed if required context cannot fit.
Large or deferred data stays behind a resource reference.

An optional candidate with `canDefer: true` that misses the current prompt budget
is eligible for a later turn in the same active `RunAsync` or `ResumeAsync`
execution. Carry-over follows the compiler's deterministic priority-and-ID order.
The live deferred queue retains at most 128 candidates. A candidate may cross
seven turn boundaries; if it still cannot fit on its eighth deferral, it is
pruned with `deferred_turn_limit`. Candidates beyond the queue capacity are
pruned with `deferred_capacity`. A candidate whose TTL expires while waiting is
pruned with `expired`. The context budget report is included even when no
candidate was selected, so these outcomes remain explicit.

`deferredIds` records scheduling decisions, not candidate payloads. Deferred
payloads are not reconstructed from the durable journal after a process crash or
after an execution returns for reconciliation. A caller that resumes such a run
must resupply every still-relevant candidate in `DurableRunContinuation.Context`;
normal TTL and budget checks run again.

Before the first turn snapshot exists, `run.input_captured` retains the initial
context candidates and active-skill references as part of the atomic run-start
batch. This only closes the initialization-to-first-turn crash window. Once a
turn snapshot has committed, normal transcript and deferred-context rules above
apply. The durable input record has a hard limit of 512 context candidates.
Constructing a durable runtime with a higher `ContextCompilerOptions.MaxCandidates`
value fails immediately; standalone context compilers may choose a different
limit.

A continuation that omits an active-skill replacement inherits the latest
durable activation. A non-empty `ActiveSkills` collection replaces it. An empty
collection is an explicit deactivation only when `ReplaceActiveSkills` is true.

## Event durability

Lifecycle transitions, initial input capture, transcript messages, turn
snapshots, action requests, and receipts are durable. Token deltas and UI
progress are ephemeral. A bounded notification consumer may drop events under
backpressure. Durable events remain available for replay from the journal;
ephemeral events do not. Consumers that need authoritative state must use the
journal rather than the live queue alone.

Deferred-tool activation changes are durable `tool.disclosure_changed` events.
An activation requested by the model is atomically paired with its tool-result
transcript message. The next turn revalidates exact version, descriptor digest,
source, visibility, and current run policy before including the tool.
