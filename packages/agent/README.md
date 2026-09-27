# @opengameagent/agent

A thin game-oriented adapter over Pi Agent. This package adds game input projection, canonical event mapping, and exact
run/turn controls without implementing another Agent loop.

`start()` captures each input field once and freezes its detached payload before activation. `control()` and `abortRun()`
capture their target and command before asynchronous work; cancellation reasons retain their original object identity.
Input, ID and clock callbacks that start another run cannot overwrite that run's activation.

Every active run segment emits owner-visible `run.metrics` immediately before `run.suspended` or `run.completed`.
Resuming deferred work starts a fresh segment and excludes the time spent suspended. All durations are non-negative
decimal microsecond strings from a monotonic clock (`metricsNow`, default `performance.now`); counts are decimal strings.
`contextAndRecall` covers request preparation and context collection, `provider` covers committed provider attempts and
explicit deferred fetches, and `native` is the remaining elapsed time, including host Tool execution.
`contextAndRecall + provider + native = total`. `durable` measures canonical event and transcript commit waits and may
overlap those phases; it is not added to that sum. Metrics and terminal publication themselves occur after the snapshot.
`firstResponse` measures the first visible assistant delta or completed content; `firstTool` measures the first canonical
Tool start. `providerFirstResponse` measures the interval from the committed attempt to its first non-empty response
observed by Pi, including private reasoning or Tool content without recording that content. It excludes admission and
queue time; it is an application observation, not a network packet timestamp. Provider duration ends at observed terminal
response, before the assistant transcript commit. `memoryPrepare`, `memorySearch`, `tool`, `approval`, and `settlement`
are measured by the framework integrations in the active asynchronous run context. These optional phases can overlap;
they are diagnostic subdivisions, not additional terms in the total. Absent observations omit their fields.
`providerAttempts` includes committed fetches; `retries` counts only
additional attempts within one model request, including `fallbacks` when its target route changes. Normal Tool
continuations and discarded attempt drafts are not retries. Metrics contain no prompt, response, Tool arguments,
provider handle, or model identity and do not replace the authoritative lifecycle event.

The canonical projector exposes provider-classified reasoning summaries only for Pi's OpenAI Responses, Azure OpenAI
Responses, and OpenAI Codex Responses APIs. It validates the provider's completed summary against Pi's terminal block,
keeps raw or redacted thinking and replay metadata private, and emits no reasoning deltas. Assistant `item.started`
content is empty; visible summary content appears only in the completed item after any durable transcript commit.

Hosts may optionally register ordered game context sources with explicit reference-count and UTF-8 byte budgets. A fresh
snapshot is injected after Pi's context transform and immediately before each provider request. The exact internal user
context and source fingerprints are committed as canonical events; loading, validation, Tool refresh, or cancellation
failure stops before provider invocation.

A context source receives the exact run/turn coordinate and returns one immutable contribution. It may pair its JSON
references with Pi-native images and position-aligned durable image references. Image contributions additionally require
explicit content-part, per-image-byte, and aggregate-image-byte limits. The adapter verifies canonical base64, media
type, byte length, SHA-256 identity, same-source reference lineage, and model image capability at the final request
boundary. The provider receives ordinary ordered text/image content, while the internal canonical event contains only
the text and immutable references. Selection and optional omission belong to the contributing extension; once an image
is contributed, the Agent never silently drops, resizes, substitutes, or retries it.

Hosts may also provide a per-run Tool execution guard. It runs immediately before each Pi Tool implementation and can
revalidate a game-owned capability, world revision, or delivery lease. A rejected guard becomes a Tool failure without
calling the underlying implementation; authoritative write tools must still use their normal durable action admission.

Hosts may optionally configure `exactToolRepeatPolicy` with their own positive safe-integer advisory and termination
thresholds; the termination threshold must be greater. There are no framework defaults. Pi's ordered
`beforeToolCall` preflight observes the fully prepared and schema-validated Tool name and canonical JSON arguments, so
raw property order and equivalent JSON numbers do not split one sequence. The tracker belongs to one active game run.
A different prepared call or a preparation failure resets it. Accepted steering and follow-up are queued by Pi; they
reset the sequence only when Pi injects that exact user message as next-turn evidence, so neither can unlock later calls
in the already requested batch. Names in the host's bounded `exemptToolNames` list are ignored without resetting it,
which permits an intentional polling Tool between two tracked calls without allowing that poll to hide a repeating
effectful call.

At the advisory threshold, the Agent emits owner-visible `tool.repeat.detected` data and injects one deterministic,
request-only internal policy message into subsequent model context while the same sequence remains active. The message
does not enter Pi history or the durable transcript. When provider provenance is enabled, every request carries an
immutable fingerprint of the complete captured policy and transform definition. An advisory request additionally links
the canonical event by an event-id alias and revision, aliases the Tool name, preserves the canonical count, and records
the exact injected content fingerprint. A host can therefore resolve the local event, rebuild the exported deterministic
advisory message, and verify the same model-context fingerprint without persisting raw identifiers in the provenance
manifest. At the termination threshold, Pi returns a normal error Tool result but does not dispatch that Tool. Every
later prepared call in the same batch is also blocked, and the run stops after the balanced Pi turn closes. If policy
event recording fails, the entire already-preflighted parallel batch fails closed with a fixed content-safe Tool error.
No Tool start record or authoritative game Action is fabricated. Event payloads contain only Tool identity, a
canonical-decimal count, and the low-cardinality action; arguments and results are never copied into the policy event.

Hosts may optionally set `maxReportedTokensPerRun` to an explicit positive safe integer. The Agent accumulates each
successfully completed assistant message's provider-reported `usage.totalTokens` exactly once within the active run,
including direct answers and Tool-bearing turns. Reaching or crossing the limit closes admission before Pi can start
another provider turn; steering or follow-up accepted before `agent_end` cannot reopen that run. The current provider
response and its admitted Tool batch are not interrupted, so this is a next-request admission boundary rather than a
prediction that prevents the response which crosses the limit. A later run starts with an independent counter.

There is no framework default, token estimate, provider-specific conversion, or billing interpretation. Missing, zero,
negative, fractional, non-finite, unsafe, or cumulatively unsafe `totalTokens` on a completed response fails the run and
prevents another provider request. Provider error and abort messages do not become completed usage observations, so
cancellation keeps its normal aborted result; deferred placeholders are accounted only when their eventual assistant
response completes. The limit composes as an additional stop condition with `shouldStopAfterTurn`.
When a run suspends on a deferred provider response while this limit is enabled, its cumulative reported-token count is
committed with the durable deferred opening and restored even if the host recreates the Agent before resuming. Resume
fails closed before provider fetch when that accounting is missing, invalid, or already meets the currently configured
limit; the host may still abandon the pending operation explicitly.

An optional durable transcript records terminal Pi messages and balanced turn boundaries. For Tool turns, the complete
assistant call is committed before Pi dispatches it, a separate start record containing the admitted execution arguments
is committed immediately before the Tool implementation, and the final Pi Tool-result message is committed before the
next provider request. A Tool that needs authoritative recovery can use the exported durable-execution hook to prepare a
stable operation identifier without performing the effect. The Agent then rechecks policy, runs the host guard, commits
the start record, and invokes that exact prepared execution. On restart, unstarted calls receive an explicit
`not_started` result; started calls are resolved through an optional authoritative receipt reader or receive
`outcome_unknown`. Restore preflights the complete repaired prefix, appends repairs in source order, and never re-executes
an old call. If cancellation interrupts a prepared durable Tool with such an operation identifier after its start
record, or the Tool throws the exported `DurableToolExecutionUnresolvedError` after effect admission may have begun,
Pi's synthetic error result is neither projected as a canonical completion nor committed as the operation's outcome.
The started call remains open for authoritative repair, and the live transcript instance must be replaced by a fresh
restore. Deterministic pre-effect failures remain ordinary Pi Tool errors.

Terminal Pi assistant messages retain ordered thinking, text, and Tool calls. Provider/API/model identity, response
identity, provider thinking effort, text and thinking signatures, redaction state, and Tool thought signatures are
preserved in an aligned replay envelope for Pi to apply exact API/provider/model replay or its normal cross-origin
transformation rules after restart.
The durable message remains authoritative: malformed or misaligned replay metadata fails restore and cannot replace
visible content, Tool identity, or arguments. Optional provider thinking effort is retained even without other replay
metadata, shares the configured byte budgets, and is excluded from canonical events and summarizer input.
Deferred output still requires a pending-operation lifecycle Adapter.
Durable user and final Tool-result images require a transcript media Adapter and remain immutable references in the log.
The Adapter re-authorizes and verifies only the final active projection before every provider request;
checkpoint-discarded prefix media is not read. A final Tool image is normalized and admitted before its durable
completion, so the live Pi turn and restart use identical bytes. If capture fails after the Tool effect, the started
effect remains unresolved for receipt-based recovery or `outcome_unknown`; Pi cannot continue with a fabricated durable
error result. Record, commit, and restore
byte/depth budgets are mandatory host configuration; the package supplies no automatic retention policy, retry policy,
or checkpoint cadence defaults.

An optional transcript compactor runs only after a completed durable turn. The host supplies the selected model's exact
context counter, summarizer, reserve, retained-context budget, and summary-output budget; no character-ratio estimate or
fallback limit is used. The summarizer may retain only Tool-balanced suffixes and must return completed,
continuation-free text from provider-neutral durable messages and safe suffix boundaries. Exact context accounting
receives the hydrated replayable Pi history and the retained suffix still uses its matching durable history, while the
summarizer receives neither replay metadata nor
assistant thinking. Compaction appends one atomic
checkpoint containing the complete replacement history while retaining the raw canonical prefix. Pi history changes only
after that append succeeds; an in-flight Tool loop receives the replacement at Pi's next-turn context boundary. Summary
or pre-append validation failure leaves the matching live history unchanged. A
checkpoint conflict, uncertain append failure, or post-commit installation failure requires restoring a fresh Agent from
the canonical head. The reserve must include the largest host-permitted next input, injected context, and Tool catalog.

After a checkpoint is committed and every external reference has been accounted for, a retention-capable store can be
trimmed with `DurableTranscript.purgeBeforeCheckpoint`. The host supplies the exact checkpoint revision, a positive
record-count batch, and every historical transcript revision that must remain addressable. Purging succeeds only when
the exact checkpoint and its source `turn.closed` record are present and the whole older prefix fits in the batch; it
keeps that source closure as the retained integrity anchor and keeps the checkpoint itself. Because the source revision's
pre-checkpoint history would no longer be reconstructable, protecting that source revision—or any earlier revision—blocks
the purge. Restore and historical materialization accept this verified retained prefix while continuing revision numbers
from the durable historical head. There is no automatic TTL, checkpoint selection, or reference discovery.
The request also requires `retainBudget: { maxCount, maxBytes }`, with explicit host-selected non-negative safe integers.
These bound the complete `retainRevisions` array and the sum of its UTF-8 string bytes before copying or deletion;
`"empty"` counts as five bytes. `{ maxCount: 0, maxBytes: 0 }` permits only an empty list. Over-budget protection fails
without trimming the transcript or silently dropping protected revisions.

The Tool source receives the immutable normalized input, run cancellation signal, and exact current run/turn coordinate
for each provider request. Tools that open a host interaction can therefore bind the request to the same Pi turn without
adding a second conversation or execution loop.

An omitted Tool `executionMode` is captured as `sequential`; only a non-durable Tool that explicitly declares `parallel`
may enter Pi's parallel execution path. A Tool with the OGA durable-execution hook must be sequential, and a contradictory
explicit `parallel` declaration fails before provider publication. Pi continues to own actual batch scheduling and
Tool-result finalization.

Optional request sources run after the base Tool snapshot at that same boundary. Each source sees only the already
admitted Tool names and may atomically contribute the same bounded context contribution plus additional Tools. Source identities
are unique across static context and request sources, every merged Tool snapshot is revalidated, and any incomplete
contribution fails before context publication or provider invocation. This seam lets Skills and other optional
capabilities filter against the exact request without receiving executable game Tool functions.

Custom input projection is asynchronous and requires explicit content-part, text-byte, aggregate canonical
image-reference-metadata-byte, per-image-byte, and aggregate image-byte limits. A multimodal projection supplies
Pi-native base64 image content together with position-matched durable
image references. The adapter verifies canonical base64, SHA-256 identity, media type, byte length, model capability, and
text/image ordering before the provider runs. Canonical events retain only the references and verified metadata, never
the encoded image bytes, a local path, or a remote URL.

Hosts that need a reconstructable local audit may attach a `providerProvenance` binding to an Agent that already has a
durable transcript. The binding names a private host scope and, when configured, immutable authorities for non-empty base
instructions and the deterministic context transformer. The request callback runs after the final context, Tool, Skill,
memory, and image projection and before the real Pi provider stream is opened. It receives an integrity fingerprint of
that exact model context, the exact transcript and checkpoint revisions, content fingerprints, image hashes and
positions, and deterministic `pv_<sha256>` aliases for the scope, stream, run, requested route, actual attempt target,
sources, references, revisions, and Tools. Raw host identifiers never enter the callback. Each committed target is a
separate attempt numbered from one within that Pi request. Discarding a target before commit creates no record and does
not consume an attempt number. A request callback failure prevents provider invocation.

Reconstruction resolves those aliases through host-retained source and lineage authorities, materializes the recorded
transcript revision with `DurableTranscript.materializeRevision()`, deterministically reapplies the named transformer and
request sources, and re-fingerprints the resulting Pi context. Historical materialization is read-only: it never repairs
an open turn, appends records, or replays an effect. It validates the complete bounded committed stream and its declared
head before returning the detached target revision, so a corrupt committed suffix cannot be presented as verified
history. Provenance is rejected at Agent construction when no durable
transcript is bound, a non-empty base-instruction fingerprint does not match, or a configured context transformer lacks
an immutable lineage reference. Transformer determinism and revision immutability are host assertions; the Agent verifies
only that the lineage reference exists and has the required shape. The manifest never receives prompts, responses,
thinking, Tool schemas, Tool
arguments/results, credentials, provider request/response IDs, errors, paths, URLs, accounts, or deferred handles.
Image media types are limited to the four protocol values rather than accepted as free-form strings, and image data at
the final provider boundary must be non-empty canonical base64.

A successful, Tool-use, or deferred Pi assistant message settles the final visible attempt only after its durable transcript
message or deferred opening has committed. Provider error and abort messages cannot enter durable assistant history; the
Agent marks that transcript instance unusable and records only their safe terminal classification. Settlement contains
only the actual served-model alias (`responseModel` when Pi supplies it, otherwise the assistant message model),
stop/outcome classification, and token accounting. An attempt adapter must settle every provider failure it consumes
before committing another target and must leave the final returned attempt for the Agent to settle. It uses the same Pi
stream contract: provider/runtime failures are returned as terminal stream events rather than thrown or rejected.
Settlement failure fails the current call but never re-sends the provider request or executes a pending Tool; the request
remains unresolved for audit. If a deferred handle was already committed, it remains available through the normal
explicit resume or abandonment API instead of being reported as a successful deferred call. Fetching or cancelling an
already-deferred operation does not create a second inference record; a later real model request after fetched Tool use
does.

`fingerprintGameModelContext()` hashes the model-visible system prompt, messages (including Pi assistant replay-origin
fields and optional provider thinking effort), and Tool names, descriptions, schemas, and constrained-sampling
configuration. It excludes timestamps, usage, cost, error text, and Tool-result details. This fingerprint, source/image
digests, and deterministic aliases are private
local correlation and integrity material, not anonymization, and must not be exported as telemetry.
