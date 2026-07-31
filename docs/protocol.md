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

An `ActionRequest` can carry `batchId`, `decisionKey`, and
`basedOnStateVersion`. These fields let a game stage actions from actors that
decided against the same snapshot, deduplicate a decision, and reject stale
proposals. They are evidence for game code; the protocol does not define a
conflict winner. Additional structured simulation coordinates can travel in the
`gameContext` extension.

A terminal `ActionReceipt` may carry `extensions.resultingGameContext`.
`GameContextReceiptEnvelope.AttachResulting` is the typed helper for producing
it. The runtime leaves the run coordinate unchanged when this extension is
absent and rejects it on an `unknown` receipt. A missing receipt-coordinate
`sessionId` inherits the current run session; an explicit value must match it.
Once every operation in the decision window is terminal, all supplied results
must agree on one final coordinate. The runtime then records
`game_context_advanced`: its payload is the updated full `AgentRun`, while its
extensions bind the exact previous and resulting coordinates plus the sorted
supporting operation IDs. This checkpoint is committed before memory derivation
and before another provider turn. Recovery replays neither inference nor host
actions for an already committed advance; it verifies the checkpoint against
the original requests and receipts.

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
this metadata directly. The reserved
`extensions.providerRoutePolicyVersion` and
`extensions.providerRoutePolicyDigest` values bind a versioned provider policy
digest into `providerRouteDigest`; recovery still accepts legacy route events
that predate those extension values and rejects partial or tampered pairs. A
custom provider should implement
`IProviderRouteMetadataSource`; providers that omit it are recorded with the
explicit `unspecified` model and `custom.streaming.v1` dialect rather than an
inferred identity.

Dispatch extensions also carry a complete versioned
`providerDialectContract`, its semantic digest, and
`providerWireRequestEvidence`. Available wire evidence contains only the
SHA-256 digest, byte length, and content type of the prepared request; an
explicit unavailable reason is used when a provider does not expose final
bytes. The evidence and its canonical integrity digest are all-or-none and
route-bound during recovery. Built-in prepared transports compute it over the
exact byte array sent. Evidence supplied by third-party providers is validated
but remains that provider's assertion.

`AgentUsage` carries optional `cacheReadTokens`, `cacheWriteTokens`,
`cacheMissTokens`, `reasoningTokens`, and `providerTotalTokens`, plus the number
of contributing `providerUsageSamples`. Missing token fields remain unavailable
instead of being rewritten to zero. Its `availability` value states whether
`costUsd` is a complete total (`cost_available`) or only known exact subtotals
within an otherwise unavailable total (`cost_unavailable`).

The runtime orders durable skill-system material before turn-specific messages
when constructing a provider request. `TurnSnapshot.stablePrefixHash` covers
only that semantic leading prefix plus the prompt-layout version, so it stays
unchanged while the prefix is unchanged. The `extensions.promptDigest` value
covers the complete turn transcript and effective tool/skill catalogs. This
separates cache-prefix identity from the dynamic request identity.

The snapshot's `providerCacheKey` extension binds the layout, stable prefix,
effective tool catalog, admitted skills, planned primary route, memory recall,
compaction, and dynamic request as separate digests. Its paired
`providerCacheDecision` reports stable-prefix break reasons separately from
dynamic-tail changes. A dynamic-tail change does not by itself make
`prefixReusable` false. Each provider-backed `budget.updated` event carries
`providerCacheUsage`: absent provider counters mean `unknown`; three explicit
zeros mean `no_activity`; positive counters produce `hit`, `write`, or `miss`.

`TurnSnapshot.extensions.conversationContextCheckpoint` is the durable,
integrity-protected identity of the exact derived provider message view. It
stores ordered message IDs and an optional runtime-created summary with source
lineage, not another copy of retained transcript content. Recovery can reuse it
only for the same run and exact admitted transcript.

Provider result extensions may contain
`providerOpaqueContinuationState` only when the application enabled durable
continuations and the provider declared the bounded envelope
`durable_non_secret`. The envelope is bound to provider, exact route digest,
and dialect state version. Absence of a new update clears prior local state;
terminal completion does not persist a new envelope.

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

Resource URIs and media types are non-empty. When supplied, the optional digest
must also be non-empty. A game that has not calculated one omits the field.

Observation admission is identity-aware. `worldId` must exactly match the run.
When an observation supplies `sessionId`, it must match the run session.
World-scoped observations may have an empty audience; every other scope must
include the run's `agentId`, and private scope must name exactly one audience.
The control plane applies these checks before accepting a steer or follow-up
and before cancelling an active provider step. Rejected controls therefore
cannot interrupt a run and then fail later during context compilation.

Games that reuse agent or entity IDs can additionally bind a restricted
observation to one exact entity lifetime. Call
`ObservationAudienceIncarnations.Attach` with one binding for every
`visibility.audienceIds` entry, and enable
`DurableAgentRuntimeOptions.RequireAudienceIncarnationForRestrictedObservations`.
The runtime then requires the active run's `gameContext.observer` to exactly
match the entity ID and incarnation associated with its `agentId`. This check
also runs for durable initial/continuation context, steer/follow-up controls,
and authoritative host observations before journal or provider-visible context
is produced. Public world observations remain unaffected. Missing, mismatched,
and malformed bindings report
`observation_audience_incarnation_missing`,
`observation_audience_incarnation_mismatch`, and
`observation_audience_incarnation_invalid` respectively. The option defaults
to `false` for compatibility.

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

Engine UI should route provider deltas through the Core attempt-safe
presentation coordinator. Presentation chunks carry run, turn,
provider-attempt, and stream-attempt identity. Retry and fallback notices name
the abandoned attempt and cause an explicit supersede/reset; the first text
from every replacement attempt is also marked `ReplacesPriorText`. The
coordinator's bounded sequence cursor can bridge a short-lived local disconnect
while the process remains alive. An expired cursor fails explicitly and
requires rebuilding the view from durable state; this convenience does not make
provider token deltas durable protocol events.

Deferred-tool activation changes are durable `tool.disclosure_changed` events.
An activation requested by the model is atomically paired with its tool-result
transcript message. The next turn revalidates exact version, descriptor digest,
source, visibility, and current run policy before including the tool.
