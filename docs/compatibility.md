# Compatibility

Compatibility is reported by evidence level. An adapter is not called verified
until its real engine or toolchain gate has passed.

| Component | Intended target | Current evidence |
| --- | --- | --- |
| Shared libraries | .NET Standard 2.1 | Windows CI runs the repository build, test, package, and performance smoke; Linux builds and tests the complete portable solution |
| Godot host | Godot 4.7.1 .NET on Windows desktop and headless | Real Godot executable, isolated addon package, C# build, scene startup, signals, structured context, durable run/resume/control, main-thread action dispatch, and shutdown are exercised |
| Unity host | Unity 2022.3 LTS or newer, Mono or IL2CPP | Package source and samples compile as .NET Standard 2.1; host conformance and package assembly gates pass without an Editor |

Linux validates the portable .NET solution; it is not a second full
engine-release matrix. Engine release gates currently target Windows.

## Unity validation boundary

The repository includes scripts for EditMode, PlayMode, Windows Mono Player, and
Windows IL2CPP Player gates. Those gates require a licensed Unity installation
with the matching platform modules and have not been executed for this alpha.
The current claim therefore covers the package contract and host implementation,
not verified Player compatibility. Mobile, WebGL, and console targets are not
claimed.

Unity DTO conversion preserves the optional `decisionKey` and `batchId`
fields used to stage simultaneous actor actions.

Godot and Unity expose optional guarded durable resume. A semantic extension
digest is validated after journal recovery and before ownership, provider,
reconciler, or host work. Custom engine backends must advertise and implement
the guarded capability end to end; requesting it through an older backend
fails closed.

## Provider compatibility

The bundled adapters target streaming chat-completions APIs that use the common
OpenAI-compatible event shape and the native Anthropic Messages API. Providers
vary in authentication, streaming details, tool-call behavior, and usage
reporting. Each configured provider must be exercised before shipping a game. A
provider token belongs in a server relay or a short-lived scoped credential for
consumer builds.

The OpenAI-compatible adapter exposes the wire differences that commonly break
tool loops instead of guessing them from a provider name. Configuration selects
`max_tokens` versus `max_completion_tokens`, whether reasoning effort requires
the vendor-specific thinking toggle, optional `tool_choice`, optional
`parallel_tool_calls`, strict function schemas, and reasoning-content replay.
Every selection is covered by the durable route-policy digest. The default
profile remains the repository's verified DeepSeek V4 Pro route; other
endpoints should set every dialect option explicitly and pass a live smoke gate
before release.

`GameAgent.Providers.Anthropic` implements the named Messages SSE flow with
client `tool_use` and `tool_result` blocks and pins the verified `2023-06-01`
API version. Configuration explicitly declares the selected route's thinking
dialect as `none`, `manual_budget`, or `adaptive`; the adapter never infers it
from a model name. Manual routes map fixed token budgets. Adaptive routes map
only allow-listed `output_config.effort` values and separately declare whether
the exact model accepts explicit thinking disable. Adaptive routes reject
non-default sampling controls, while manual routes enforce their
thinking-specific sampling restrictions. These declarations are covered by
the durable route-policy digest.

The adapter supports text, client tools, automatic prompt-cache control, and
bounded thinking/redacted-thinking output.
Extended thinking is currently rejected before transport when client tools are
exposed because Anthropic requires the signed thinking block to be replayed
unchanged on the next tool-result request; this adapter does not pretend that a
plain reasoning string satisfies that provider-private continuation contract.
An adaptive route therefore rejects tool use unless thinking is explicitly
disabled on a route that declares disable support.
Media, fallback blocks, and server-side tools are rejected explicitly.
Tool-input JSON is accumulated incrementally and parsed only when
its content block closes. Usage is emitted once from final cumulative counters;
cache reads, writes, and misses remain unavailable when the response does not
supply a complete cache-counter pair.
Configured cost remains unavailable for nonzero cache creation unless the
response also supplies the exact 5-minute/1-hour breakdown and every applicable
rate is configured.
The direct transport requires HTTPS, disables redirects, sends credentials only
through `x-api-key`, and fixes the request path to `/v1/messages`.

Custom streaming providers should also implement
`IProviderRouteMetadataSource` with their exact model identifier and versioned
transport dialect. Route-sensitive providers should use the four-argument
`ProviderRouteMetadata` constructor and supply a versioned, non-secret SHA-256
policy digest covering endpoint identity and request, response-accounting, and
pricing policy. If the route implements `IProviderPromptTokenEstimator`, the
digest should also cover its estimator identity and version. The runtime
combines that policy identity with the capability
snapshot used for the attempt and journals a deterministic route digest. A
change to any covered policy therefore produces a different durable route
identity. The two-argument constructor remains compatible and marks the policy
as explicitly unspecified. If the optional metadata interface is absent, the
durable dispatch records `unspecified` and `custom.streaming.v1`; it never
guesses a fallback model.

A provider may additionally implement `IProviderRequestAdapter`. The adapter
receives a deep-snapshotted, request-only view and a capability profile. It may
remove unsupported reasoning or repair provider-required tool pairs, but it
cannot change run/turn/attempt identity, replace the authorized tool set, or
increase an output-token limit. `ProviderCapabilities` also carries bounded
tool-count and aggregate schema-byte limits so an incompatible fallback is
rejected before network dispatch.

Custom adapters return transformed output through
`ProviderRequestPreparationContext.CreatePreparedRequest`. That public factory
binds the evidence report to the runtime-owned input baseline and computes both
digests; `ProviderRequestPreparationChanges` carries only non-negative repair
counters. Adapter invocation, output validation, and the final deep copy share
one timeout and one quarantine slot.

Provider input is rejected before deep snapshotting when it exceeds 4,096
messages, 65,536 content parts, 4,096 tools, 131,072 JSON nodes, or 8 MiB of
measured content. Adapter output is checked against the same bounds before it
is deep-copied. Adapter-owned message and tool lists are first frozen through
one exact, bounded indexed read: their enumerators are never trusted and their
`Count` values are never re-read. Limit checks, protected-field digests,
preparation evidence, sanitization, and dispatch then consume only
runtime-owned snapshots. The bundled HTTP adapter additionally caps the fully
JSON-escaped request body at 8 MiB, so escaping cannot turn a bounded logical
request into an unbounded transport allocation.

Retries require trustworthy usage semantics. Explicitly rejected requests may be
declared known-zero. A failed attempt with reported usage is charged before the
next attempt. A dispatched attempt with unknown usage fails closed and is marked
as unaccounted in the durable run instead of silently moving to another provider.
The dispatch intent itself is journaled before provider code runs, so a process
crash before the usage callback is also detected during recovery.

Custom providers classify failures with `ProviderFailureDisposition`.
`AbortRun` is for request-wide validation, policy, and other failures that no
route can safely repair. `Failover` is for a route-local rejection that should
not be repeated on the same route. `RetryThenFailover` is for transient
route-local failures. The legacy Boolean `retryable` constructor remains source
compatible and maps to `AbortRun` or `RetryThenFailover`.

## Persistence compatibility

Current memory stores can read format version 1 without rewriting it. The first
successful mutation appends a version 2 frame, after which a version-1-only
runtime cannot reopen that file. If runtime rollback must remain possible, back
up the memory file or upgrade a copy under a new path before allowing writes.
The current reader supports pure version 1 history, pure version 2 history, and
mixed version 1 followed by version 2 history; the older reader supports only
pure version 1 history.

The bundled HTTP adapter treats credential rejection, exhausted balance,
missing or retired endpoints, and rejected redirects as route-local known-zero
failures. Invalid request payloads remain request-fatal. Ambiguous timeouts and
server failures still fail closed when no trustworthy usage report exists.

Route-local failures feed a bounded, process-local circuit keyed by route
digest. `ProviderRouteResilienceOptions` configures the initial cooldown,
maximum exponential cooldown, and maximum tracked routes. Only one half-open
probe is admitted after a cooldown; other concurrent NPC runs continue to
fallback routes. Set `Enabled = false` only when an application deliberately
wants every run to try the primary route again.

Provider usage keeps cache-read, cache-write, cache-miss, reasoning, and
provider-total token counts as nullable values. `null` means the provider did
not report that dimension; zero means it explicitly reported zero. Aggregation
keeps a dimension only when every contributing sample supplied it. The
`availability` field applies to the total cost: `cost_available` is
authoritative, while `cost_unavailable` means `costUsd` is at most the sum of
known exact subtotals and must not be used as the total. Both runtimes fail the
cost budget closed when total cost is unavailable.

The bundled adapter never converts a missing cache breakdown into a cache miss.
It derives an exact configured cost only when the read/miss split is available
or both configured input rates are identical. An optional cache-write rate can
be configured with `InputCacheWriteUsdPerMillionTokens`; when that rate is
configured, the corresponding write-token count must be present for total cost
to be available.

## In-process callback boundary

The runtime cannot forcibly terminate arbitrary game or plugin code in the same
process. It instead bounds the damage. Core cancellation domains have separate
admission lanes backed by fixed control-plane, data-plane, and per-extension-domain
worker classes, so one extension cannot consume workers reserved for shutdown or
another extension. Engine data-plane cancellation and synchronous event observers
retain their own bounded worker boundaries. A blocked callback retains its slot
until it actually returns. When a boundary is full, no additional worker is
created. Authoritative durable events remain replayable from the journal.

Godot and Unity lifecycle owners reserve future cancellation capacity before
they can accept work. Each lifecycle lane currently admits 72 owners per
engine adapter: eight cancellation workers plus 64 queued owners. Godot request
cancellation uses a separate process-wide lane with eight workers and 4,088
queued reservations; Unity run cancellation likewise has a separate large
data-plane lane and a distinct shutdown-promotion lane. A reservation is
returned only after the real cancellation and owner-drain tasks finish, even
when the public shutdown wait has already timed out. Lifecycle teardown cannot
therefore be displaced by the configured per-runtime active-run range.
Core execution routers use the same bounded lifecycle dispatcher. Disposal
waits for a lease only while that router still owns active work; natural drain
wins the race and releases the router without expanding another runtime's
blocked-callback failure domain.

Operation reconciliation follows the same fail-closed rule. At most 64 queries
may be detached process-wide, and the same world/run/operation identity cannot
be queried twice concurrently. A cancelled caller may resume later, but an
uncooperative prior query must finish before that identity is admitted again.

## Headless lifecycle boundary

The compact headless loop does not resume an existing run. A run ID is
single-use within its session store: an active duplicate is rejected by the
runtime instance, and a completed ID is rejected when journal history is found.
Each admitted run owns an event sequence beginning at zero.

Each headless runtime instance also has a default in-process limit of 256 active
runs and 256 in-flight host actions. An action continues to occupy its slot
after timeout or cancellation until the host task actually finishes.
Cancellation callback cleanup is observed independently and cannot hold a
finished action slot. `HeadlessAgentRuntimeLimits` also bounds the observation
count, tool count, aggregate encoded input bytes, and JSON shape before provider
dispatch. Applications can lower these limits; engine adapters can enforce a
stricter host-facing limit as well. This boundary is fail-fast and is not a
distributed quota.

Each headless action is bounded by the smaller of its tool timeout and remaining
run deadline. Time spent persisting the write-ahead request consumes that same
absolute deadline; an expired request is failed without host dispatch. If the
in-flight cap is full, the request receives `action_capacity_exceeded` without
host dispatch. If a dispatched host action ignores cancellation, the call
returns `reconciling` with the operation ID; a late receipt is not silently
adopted by that call.

The history check and first append are not an atomic cross-process operation.
Multiple runtime instances that share a session store must use an external
run-ID admission mechanism, or use the durable runtime, when they can start the
same run concurrently.

## Versioning

The framework is pre-1.0. Protocol schemas carry explicit versions, but source
and wire compatibility may change between alpha releases. Pin exact package
versions and run the repository conformance fixtures when upgrading.

Composite semantic identities use a versioned, typed, length-delimited digest
scheme across the runtime, independent of the durable-store implementation.
Strings must be well-formed Unicode and are encoded with strict UTF-8, so
replacement fallback cannot alias distinct invalid inputs.
The current file store writes format 3 frames and can read format 1 and 2
history. Completed older history remains available for audit, and a store may
append a new format 3 run after that history. A nonterminal older run whose
resume contract contains a digest from the previous scheme fails closed;
create a new run instead of rewriting or silently migrating its identity.
Custom durable stores must enforce the same restart rule even though they do
not use the file-journal frame format.
