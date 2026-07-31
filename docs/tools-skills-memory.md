# Tools, skills, and memory

## Tools

Tools are registered as immutable snapshots. A turn captures one snapshot; a
registry replacement affects only later turns.

`ToolDescriptor.visibility` is `direct`, `deferred`, or `internal`. Direct tools
are sent to the provider on every authorized turn. Internal tools never enter
model-facing search, activation, or provider schemas. Deferred tools use a
bounded two-step disclosure flow:

1. `runtime_tool_search` searches only deferred descriptors authorized by the
   current run's `IToolDisclosurePolicy`. Results contain a small summary plus
   exact `name`, `version`, descriptor digest, and source identity.
2. `runtime_tool_activate` accepts that exact identity. A successful activation
   becomes callable on the next provider turn through the ordinary provider
   schema, argument validator, scheduler, write-ahead action request, and host
   receipt path. It is not a generic proxy. Calling the newly activated tool in
   the same provider response still returns `unknown_tool`.

The default disclosure policy permits registered deferred tools. A custom
policy is installed with
`GameAgentRuntimeBuilder.WithToolDisclosurePolicy(...)`; exceptions and invalid
decisions fail closed. Search is deterministic for Unicode text and bounded by
`DurableAgentRuntimeOptions.ToolDisclosureLimits`, which also caps activated
tools and control calls per turn. The two reserved control names cannot be
registered as game tools.

Activation state is isolated per run and durably stores the exact
`name@version`, descriptor digest, source, and origin. Recovery never retargets
that state. If the descriptor disappears, changes without a matching digest, or
fails policy revalidation, the activation is revoked and the removal is
persisted before provider dispatch. An explicit activation of the new exact
identity is required.

`retryPolicy` and `idempotencyPolicy` are validated contract metadata. In this
alpha, they do not cause the runtime to automatically repeat a game tool or host
action. Provider-attempt retry is a separate mechanism. `timeoutMs` must be from
1 through 86,400,000.

The runtime validates model arguments against a bounded strict JSON Schema
subset before game code is called. Supported keywords include:

- `type`
- `properties`
- `required`
- boolean `additionalProperties`
- `enum` and `const`
- numeric `minimum` and `maximum`
- `minLength` and `maxLength`
- `items`, `minItems`, and `maxItems`

Unsupported keywords fail closed. Error objects contain codes and JSON paths,
never the rejected values.

Successful host results are checked against `resultSchema` when one is declared.
If the host committed an action but returned a nonconforming result, the runtime
preserves the authoritative success status, removes the invalid payload, marks it
non-retryable, and reports `tool_result_schema_invalid` to the next model turn.

Conflict scopes may reference validated argument fields:

```text
entity:{entityId}
inventory:{owner.id}
```

Values are UTF-8 bounded and percent-encoded. Trusted runtime bindings such as
`agentId`, `worldId`, `runId`, and `turnId` cannot be spoofed by model arguments.

### Timed-out host executions

A timeout bounds the agent loop, but it cannot forcibly stop arbitrary host
code. A durable action deadline starts before its write-ahead journal append.
Journal, queue, parallelism, effect-barrier, and conflict-lock wait all consume
that same absolute deadline. If it expires before host dispatch, the scheduler
returns `tool_deadline_expired` with `MayHaveExecuted = false`. When a dispatched
host executor ignores cancellation, the scheduler returns `tool_timeout` and
keeps the original parallelism, conflict, and effect leases until that executor
actually finishes. It also places the execution in a temporary quarantine:

The timeout wait is established before host dispatch. If an injected timeout
service fails synchronously, the scheduler returns
`tool_timeout_infrastructure_exception` with `MayHaveExecuted = false` and does
not invoke the host.

- the same exact tool name and version is rejected before host dispatch with
  `tool_dispatch_blocked_by_detached_execution`;
- calls that can conflict with a detached side effect are rejected with
  `tool_dispatch_blocked_by_detached_side_effect`;
- a detached world or external write is a global barrier, so every later tool
  call is rejected until it finishes;
- a detached agent-local write blocks later writes and reads with overlapping
  resolved conflict keys, while unrelated pure reads may still run.

These failures have `MayHaveExecuted = false`. Quarantine is removed
automatically only after the detached executor completes; late exceptions are
observed and never replace the already returned timeout.

`ToolBatchScheduler.DetachedExecutionCount` and
`GetDetachedExecutionSnapshot(...)` expose a bounded diagnostic census with
identity, effect, reason, and timing only; arguments and results are never
retained. `ToolSchedulerLimits.MaxDetachedSnapshotItems` caps each snapshot.
`DrainDetachedExecutionsAsync(timeout, cancellationToken)` requires a finite
timeout and lets a host wait for quiescence without making shutdown unbounded.
Stop new run admission before draining, because the drain signal describes the
currently quarantined set and does not prevent new tool work.

The standard runtime shutdown does this automatically: it closes new-run
admission, cancels and drains active runs, then waits for detached tool work for
at most `ToolSchedulerLimits.DetachedShutdownDrainTimeoutMs` (1,000 ms by
default, configurable from 0 through 60,000). A timeout never makes shutdown
wait forever; journal flush and owned-resource disposal continue while the
remaining executor stays quarantined until it actually returns. Such host code
cannot be forcibly terminated by the runtime. Standard-builder users can
inspect `DurableAgentRuntime.DetachedToolExecutionCount`,
`GetDetachedToolExecutionSnapshot(...)`, and the nullable
`DetachedToolExecutionsDrainedOnStop` result.

Complete conflict keys are part of the game's tool contract; incomplete keys
can make scoped concurrency unsafe.

### Per-turn side-effect admission

`DurableAgentRuntimeOptions.MaxSideEffectToolCallsPerTurn` optionally limits
non-`pure_read` calls in one provider response. The default is `null`, which
preserves unrestricted batching. A game that requires the model to observe one
authoritative receipt before proposing its next mutation can set the value to
`1`; setting it to `0` makes the agent read-only.

If a response exceeds the configured value, the runtime rejects every
side-effecting call from that response with
`side_effect_tool_call_limit_exceeded` before `tool.started`, write-ahead
`ActionRequest`, or host dispatch. Valid pure reads in the same response still
execute, and all results are returned to the next provider turn so it can
replan from evidence. Rejecting all writes rather than accepting the first one
keeps outcome independent of provider call ordering.

The effective limit is captured in the durable `TurnSnapshot` before provider
dispatch. Admission therefore uses the policy that produced that turn, not a
later mutable runtime configuration. This is a scheduling policy only; the
game host still validates every admitted action against business rules and
current world state.

### Semantic no-progress guard

The durable runtime enables a bounded semantic tool-loop guard by default.
`DurableAgentRuntimeOptions.ToolLoopGuard` controls it. The first stable outcome
establishes a baseline; the default warning appears after two identical
repetitions, and the run fails with `tool_no_progress` after four identical
repetitions. Both thresholds count repetitions after the baseline.

The guard compares the canonical tool name and arguments together with the
captured tool version, effect, and descriptor digest. Those optional identity
fields are stored on the durable assistant tool-call record, so recovery uses
the descriptor that the model actually saw instead of retargeting history to
the current catalog. Older records remain readable.

Only two cases accumulate:

- the same terminal failure or rejection for the same call signature;
- the same successful result from a captured `pure_read` tool.

The guard also detects argument churn: the same versioned tool repeatedly
receiving different arguments while producing the same non-progress outcome.
This path is deliberately slower than exact-call detection. By default it
warns after four argument changes following the baseline and stops after
eight. `DetectArgumentChurn`, `ArgumentChurnWarningRepetitions`, and
`ArgumentChurnHardStopRepetitions` configure or disable it. A changed outcome
resets the churn pattern.

A successful write, a non-null state diff, authoritative observations, a
revision change, or a changed read result is progress and resets accumulated
patterns. Unknown, pending, malformed, oversized, or legacy successful results
without captured effect evidence fail open and never cause a semantic stop.
Normal `AgentBudget.MaxTurns`, `MaxActions`, token, cost, and duration limits
still bound those fail-open paths.

Warnings are durable user messages added to the next provider turn. They
contain only the tool name, pattern kind, canonical digests, repetition count,
and stable reason code; they never copy tool arguments or results. At the hard
threshold the runtime commits the failed run before charging or dispatching
another provider turn.
After a process loss at the preceding clean turn boundary, the guard rebuilds
from the bounded durable transcript and reaches the same decision without
replaying the action.

## Skills

A skill manifest contains:

- versioned identity and digest;
- description and prompt fragments;
- required and optional tools;
- context-provider references;
- resource references;
- capability requirements, trust, and activation policy.

The runtime sends a bounded catalog plus full data only for activated skills.
Catalog search exposes only admitted ID, version, digest, and description; it
never exposes prompt fragments, tools, context references, or resources.
The built-in `runtime_skill_search` control accepts either a bounded string or
a structured JSON query. `runtime_skill_activate` requires the exact
ID/version/content-digest tuple returned by the captured catalog. Activation is
re-admitted against the same trust, capability, host-policy, required-tool
version, and tool-disclosure boundaries, and becomes effective on the next
provider turn. Before admission can activate a required tool or write any
checkpoint, the runtime validates the complete proposed active set against
fragment, prompt-byte, reference, and active-skill limits. A rejected activation
therefore leaves both durable skill state and deferred-tool disclosure
unchanged, and the next provider turn remains usable. Catalog and prompt budgets
are independent. Skill and tool registries expose monotonic generations and
canonical digests.

Skill prompt fragments are privileged system input, so activation passes
through `ISkillAdmissionPolicy` before a provider request is built. The default
policy is deliberately narrow:

- only `builtin` and `trusted` skills can be disclosed;
- full skill content is used only when the exact skill version appears in the
  effective run or continuation `ActiveSkills` list;
- `capabilityRequirements` and `activationPolicy` must be empty objects because
  the default policy does not pretend to enforce application-specific fields;
- every `requiredToolRef` must use `name@version` and match that exact version
  in the immutable tool snapshot captured for the same turn.

An explicitly active skill may use a matching direct required tool immediately.
A deferred required tool is auto-activated for that same provider turn only
when disclosure policy permits it and the activation cap can admit the entire
required set. Internal tools, policy denial, a stale descriptor digest, or
capacity exhaustion fail the skill before provider dispatch with a stable
reason code. Optional tool references never auto-activate.

`ResumeAsync(runId)` inherits the latest durable active-skill disclosure so a
process restart or a crash at a clean turn boundary does not silently remove
instructions. A non-empty continuation `ActiveSkills` collection replaces that
activation. To explicitly deactivate every skill, pass an empty collection and
set `DurableRunContinuation.ReplaceActiveSkills` to `true`.

Exact dynamic activation state is stored in the run checkpoint and turn
snapshot. A successful model activation atomically commits that checkpoint,
the resulting deferred-tool disclosure state, and the tool-result transcript.
Recovery revalidates the exact ID/version/content digest and rejects a changed
catalog entry or a partial/inconsistent activation batch.

An untrusted or otherwise unsupported inactive skill is omitted from the system
catalog. Requesting it as active fails the run with a stable skill-admission
reason code before provider dispatch. Required-tool presence and exact version
matching are runtime invariants and cannot be bypassed by a custom policy.

Games that can actually evaluate trust, capabilities, or activation rules may
implement `ISkillAdmissionPolicy` and inject it with
`GameAgentRuntimeBuilder.WithSkillAdmissionPolicy(...)`. A custom policy can
explicitly admit declarations that the default does not interpret and records
its own stable allow/deny reason. Policy evaluation should be deterministic,
bounded, and non-blocking; the request exposes immutable turn identities and
the captured skill and tool data. Each durable `TurnSnapshot` stores the policy
identity and version, an admission digest, and admitted active-skill decisions
with their content digests.

Declared `contextProviderRefs` and `resourceRefs` are resolved only through an
explicit `ISkillContentResolver`, configured with
`GameAgentRuntimeBuilder.WithSkillContentResolver(...)`. The core runtime
performs no file or network access. Every declared reference is required:
missing resolvers, resolution errors, timeouts, malformed JSON, digest/size
mismatches, depth/count/byte overflow, and concurrency-cap exhaustion fail
closed before provider dispatch.

Resolved content is measured as canonical JSON UTF-8 bytes. A declared or
resolver-reported digest, when present, is either 64 lowercase SHA-256 hex
characters or `sha256:` followed by that value and must match the runtime's
recomputed digest. Declared and reported `SizeBytes` values refer to the same
canonical JSON UTF-8 bytes. Successful items are injected as a bounded
`user`-role, `non_authoritative`, `context_only` envelope rather than as system
instructions. Count, item bytes, aggregate bytes, reference depth, JSON depth,
JSON nodes, per-reference timeout, and concurrent resolver calls all have
independent limits. A resolver that ignores cancellation retains its slot and
is reported by `DetachedSkillContentResolverCallCount`; shutdown records its
bounded result in `SkillContentResolversDrainedOnStop` without waiting forever.
Raw context-provider references and resource URIs remain inside the host
resolver boundary. Provider envelopes and durable resolution evidence identify
them only by a domain-separated SHA-256 `referenceDigest`, kind, traversal
depth, and—when safely derivable—a strictly validated, parameter-free ASCII
media `type/subtype`. Media-type parameters and malformed media-type values
remain bound into `referenceDigest` but are not disclosed or persisted, so
content access does not also reveal a private path, internal address, or query
token.

The `toolDisclosure` turn-snapshot extension records the disclosure policy
identity/version, base direct descriptors, active deferred identities, the
effective provider-tool digest, the digest of currently authorized but still
hidden deferred descriptors, durable state digest, and decision/reason digests.
All entries come from the immutable catalog captured for that turn.

`SkillManifestImporter` is the build/editor ingestion boundary. It accepts
closed-world JSON documents, validates each manifest independently, keeps valid
documents when another file is broken, and reports stable source-bound errors
for invalid contracts, duplicate source IDs, and duplicate skill references.
Untrusted manifests receive a warning before the normal runtime admission gate.
The importer does not download or execute skill content.

`SkillManifestImportOptions` bounds document enumeration, aggregate UTF-8 input,
retained manifests, and retained diagnostics. Per-document validation failures
remain source-bound diagnostics. Batch resource limits fail closed with stable
`skill_import_document_count_exceeded`, `skill_import_bytes_exceeded`,
`skill_import_manifest_count_exceeded`, or
`skill_import_diagnostic_count_exceeded` exception codes. Enumeration reads at
most one item beyond `MaxDocuments`, solely to detect overflow.

### Local skill packages

`GameAgent.Persistence.LocalSkillPackageCatalog` adds an explicit, host-owned
filesystem boundary for inert local packages. Each `LocalSkillPackageSource`
names one root and one host trust level. Trust defaults to `Untrusted`; the
loader replaces the manifest's `trust` value with that host value before
import. A package can therefore request no privilege by writing `trusted` or
`builtin` into its own `skill.json`.

`Reload` performs a bounded recursive discovery of files named exactly
`skill.json`, requires strict UTF-8, and runs the effective documents through
`SkillManifestImporter`. Source count, scanned entries, directory depth,
manifest count, manifest bytes, resource count and bytes, aggregate bytes,
relative-path bytes, JSON depth/nodes, and retained diagnostics all have
independent limits. Roots are never created automatically. Discovery supports
Windows and Linux and rejects root or descendant symlinks, junctions, and
reparse points. Files are opened without following the final link, then the
native handle's final path and file identity are checked against the expected
contained path before and after the bounded read.

Publication is all-or-nothing. Any discovery, import, path, identity, resource,
or duplicate-reference error returns source-bound diagnostics and leaves the
current `SkillCatalogRegistry` and resource snapshot untouched. A successful
candidate calls `SkillCatalogRegistry.Replace` once; reloading identical bytes
keeps the catalog generation unchanged. `LocalSkillPackageInfo` reports the
effective trust, exact manifest-file digest, package source digest, and final
skill content digest without exposing an absolute path. For local packages the
manifest's effective declared digest is the loader-computed source digest,
which binds the source identity, effective trust, manifest bytes and semantics,
and every declared resource's raw-file and canonical-content digest.

Local `resourceRefs` must be portable relative paths beneath the package
directory. Only strict-UTF-8 JSON media types and `text/*` media types are
accepted. Reload reads and pins them into a bounded in-memory snapshot;
resolution performs no later file or network access. JSON and text are returned
inside `application/vnd.game-agent.skill-package-resource+json` data wrappers.
Declared resource digests and sizes, when present, refer to the canonical JSON
wrapper and are verified before publication. The catalog implements
`ISkillContentResolver`, so a host may pass the same instance to
`WithSkillContentResolver(...)`; runtime-builder registration remains explicit.
There is no file watcher, package execution, assembly loading, installer, or
network downloader. Hosts should invoke `Reload` at a controlled turn boundary.

Bind the loader and runtime to the same host-owned registry:

```csharp
var skillRegistry = new SkillCatalogRegistry();
var localPackages = new LocalSkillPackageCatalog(
    skillRegistry,
    new[]
    {
        new LocalSkillPackageSource(
            "project-skills",
            packageRoot,
            SkillPackageSourceTrust.Untrusted)
    });

LocalSkillPackageReloadResult reload = localPackages.Reload();
if (!reload.Applied)
{
    // Present reload.Diagnostics and keep the last-known-good catalog.
}

BuiltGameAgentRuntime built = new GameAgentRuntimeBuilder(gameHost)
    .UseFileJournal(journalPath)
    .AddProvider(provider)
    .WithSkillRegistry(skillRegistry)
    .WithSkillContentResolver(localPackages)
    .Build();
```

`WithSkillRegistry` does not replace the registry snapshot. A later successful
`Reload` therefore becomes visible to new turns, while an in-flight turn keeps
the exact catalog snapshot it captured at admission. Static `WithSkills` and
`WithSkillRegistry` are mutually exclusive so configuration order cannot
silently discard a catalog.

## Memory

`IMemoryProvider` is the retrieval boundary. `IMemoryStore` adds compatible
single-record upsert and delete operations. `IAtomicMemoryBatchStore` adds
all-or-nothing mixed upsert/delete batches.

`DeterministicMemoryStore` is a bounded local baseline:

- no embedding model is required;
- strings and structured JSON are tokenized deterministically;
- queries are scoped and may require tags;
- expired records are ignored;
- result count and UTF-8 bytes are bounded;
- ranking is stable.

`FileMemoryStore` is the durable version of that baseline. It uses an
append-only, checksummed local file, flushes each acknowledged mutation,
recovers committed upserts and deletes after restart, truncates an incomplete
final frame, and rejects committed corruption. It requires no embedding model
or native database. One writer owns a file; calls on that writer are
serialized. Optional expected revisions provide compare-and-swap when several
game systems may race to update memory.

`DeterministicMemoryStore` and `FileMemoryStore` both implement
`IAtomicMemoryBatchStore`. Create operations with `MemoryMutation.Upsert(...)`
and `MemoryMutation.Delete(...)`, then call `ApplyAtomicBatchAsync`. Duplicate
memory IDs, null members, empty batches, batches over 1,024 mutations, and
aggregate content over 8 MiB fail before state changes with a stable
`MemoryBatchValidationException.ReasonCode`. A delete and upsert for the same
ID must be split into separate batches; this prevents order-dependent
interpretation of malformed retries.

The file store also exposes `ApplyAtomicBatchWithRevisionAsync` for optimistic
compare-and-swap. One mixed batch consumes one revision and one mutation frame.
Its final commit marker covers the entire batch, so startup recovery exposes
either every member or none. A batch containing only deletes for missing IDs is
a no-op and consumes neither a revision nor file space. Cancellation or
validation/capacity failure before the write boundary leaves the prior state
untouched. An I/O error after writing starts faults the open instance; reopen
the file to resolve the complete-frame-or-torn-tail outcome.

`RuntimeMemoryLifecycle.CommitAtomicBatchAsync` exposes the same operation
through the runtime-managed memory boundary. As with `CommitAsync`, every
upsert member must carry committed provenance. The whole batch is rejected
before the store is called if any upsert is uncommitted. A custom write store
that implements only `IMemoryStore` keeps its existing single-write behavior;
requesting a batch fails explicitly with `MemoryBatchNotSupportedException`.
Runtime outbox writeback has the stronger
`IIdempotentAtomicMemoryBatchStore` requirement and fails with
`MemoryIdempotentBatchNotSupportedException` when durable deduplication is not
available.

The file store defaults are 10,000 live records, a 1 MiB frame payload,
256 MiB of log data, and 100,000 mutation frames. They bound live state,
single-commit size, and startup replay independently. Deletes and overwrites
still consume mutation frames because the history is append-only. Crossing any
configured capacity fails before the mutation is written and never evicts
memory automatically.

```csharp
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;

var memoryPath = Path.Combine(gameUserDataDirectory, "agent-memory.gam");
await using var memoryStore = new FileMemoryStore(
    memoryPath,
    new FileMemoryStoreOptions
    {
        ProviderId = "save-slot-memory",
        Capacity = 25_000
    });
await using var memory = new RuntimeMemoryLifecycle(
    new IMemoryProvider[] { memoryStore },
    writeStore: memoryStore);

var committed = new MemoryRecord(
    memoryId: "npc-17:bridge-warning",
    scope: "npc:npc-17",
    content: ProtocolJson.ParseElement(
        """{"fact":"the north bridge is closed"}"""),
    tags: new[] { "bridge", "warning" },
    importance: 80,
    createdAt: DateTimeOffset.UtcNow,
    updatedAt: DateTimeOffset.UtcNow,
    provenance: new MemoryProvenance(
        worldId: "world-1",
        sessionId: "save-slot-3",
        saveRevision: 42,
        sourceRunId: "run-9",
        sourceEventId: "event-31",
        committed: true,
        timelineId: "main"));

await memory.CommitAsync(committed);
var recall = await memory.RecallAsync(
    new MemoryQuery(
        scope: "npc:npc-17",
        query: ProtocolJson.ParseElement("""{"bridge":"closed"}"""),
        worldId: "world-1",
        sessionId: "save-slot-3",
        maximumSaveRevision: 42,
        requireCommittedProvenance: true,
        timelineId: "main"));
```

The game should place the file inside the save/profile boundary that owns the
memories. Copy, delete, or roll back that file with the corresponding save.
The store is not encrypted, so memory content must not contain provider keys
or other credentials.

Compaction is explicit and offline: stop the writer, rebuild live records from
application-authoritative data into a new store, validate it, then let
application code switch files and archive the old one. The runtime does not
provide an unbounded export-all call and never performs destructive automatic
compaction or rotation.

`MemoryProvenance` binds a record to a world, optional session, save revision,
source run/event, and committed state. Queries can require committed provenance
and exclude records from another world or a future save revision.
When provenance omits a timeline but the record has a `GameTimeWindow`, that
window supplies the timeline for game-time recall. A timeless record with no
timeline remains excluded from timeline-bound queries; the runtime never treats
an unspecified timeline as matching every branch.

Perspectival records are fail-closed. Without a query `Observer`, they are
excluded; with one, only the same entity incarnation's records are included.
Non-perspectival records remain available as shared facts.
`IncludeAllPerspectives` defaults to `false` and is reserved for trusted
system-level operations that intentionally need cross-perspective recall.

`RuntimeMemoryLifecycle` adds bounded multi-provider recall, deterministic
deduplication, fail-soft provider diagnostics, keyed prefetch, one-time
prefetch consumption, and bounded shutdown. Runtime-managed writes require
committed provenance. Memory is still derived and untrusted: it cannot settle
an action request or replace a host receipt.

Multi-provider ranking defaults to the historical raw-score comparison. That
is appropriate when providers share one score scale. Set
`MemoryLifecycleOptions.RankingMode` to
`MemoryRankingModes.ReciprocalRankFusion` when combining lexical, vector,
graph, or remote providers whose raw scores are not comparable. The lifecycle
then uses only each provider's bounded deterministic rank, sums repeated memory
IDs, and preserves partial-provider diagnostics.

Every recall report also contains bounded, content-free candidate evidence:
the final score plus provider ID, provider-local rank, and raw score. Runtime
recall events retain this evidence for at most 32 selected candidates and eight
provider contributions per candidate, along with truncation and total-count
fields. Memory content is not copied into the diagnostic payload.

`VectorMemoryStore` is the optional semantic path. It accepts a game-supplied
`IMemoryEmbeddingProvider`, bounds dimensions, resident vector values,
concurrent embedding calls, embedding time, and search comparisons, validates
every returned float, and ranks normalized cosine similarity. It supports
atomic and idempotent atomic batches, so it can be the runtime write store.
Nothing creates or downloads an embedding model automatically.

An embedding provider declares a stable provider ID, model ID, version, and
dimension count. The store captures that identity at construction and checks
it before and after every embedding call. A hot-swapped model or changed
dimension therefore fails closed instead of silently mixing incompatible
vectors. Rebuild the derived vector index explicitly when upgrading an
embedding model.

```csharp
var semantic = new VectorMemoryStore(
    gameEmbeddingProvider,
    capacity: 10_000,
    options: new VectorMemoryStoreOptions(
        maxVectorValues: 20_000_000,
        minimumSimilarity: 0.15));

await using var memory = new RuntimeMemoryLifecycle(
    new IMemoryProvider[] { lexicalMemory, semantic },
    writeStore: semantic,
    options: new MemoryLifecycleOptions
    {
        RankingMode = MemoryRankingModes.ReciprocalRankFusion
    });
```

All providers in a hybrid set must observe the same committed derived-memory
feed. The lifecycle has one configured write store and does not silently mirror
writes into unrelated indexes. A game that also persists lexical memory should
rebuild the in-memory vector index from application-authoritative derived data
on load, or provide one store that updates both indexes under its own recovery
contract.

Custom providers should honor query bounds to avoid wasted I/O. The lifecycle
also enforces a hard result count per provider and retains only a bounded,
deterministically ranked candidate set, so a misbehaving provider cannot make
the runtime materialize an unbounded merge. Before recalled records enter an
agent turn, runtime integration independently reapplies the bound query and
rejects records from another session.

Games can replace or combine it with full-text, graph, database, or hosted
memory without changing the agent loop. `IRuntimeMemoryPolicy` is the explicit game
boundary that selects a query and derives writes:

```csharp
await using var memory = new RuntimeMemoryLifecycle(
    new IMemoryProvider[] { memoryStore },
    writeStore: memoryStore);

await using var built = new GameAgentRuntimeBuilder(gameHost)
    .UseFileJournal(runtimeJournalPath)
    .AddProvider(modelProvider)
    .WithRuntimeMemory(
        memory,
        new NpcMemoryPolicy(),
        disposeOnShutdown: false)
    .Build();
```

`PlanRecall` receives an immutable run/turn coordinate, committed transcript
snapshot, and pending game context. It may return a `RuntimeMemoryRecallPlan`
or `null`. A plan can consume a one-time prefetch key and falls back to its
query if the key is absent. Partial multi-provider recall fails the run with
`memory_recall_incomplete` unless that individual plan explicitly accepts
partial results.

The runtime binds every policy query to the current run's world and prevents a
policy from selecting another session. When a game coordinate is present, it
also prevents recall beyond the current save revision, timeline, observer
incarnation, or game time. A null query session still admits world-global
records, but session-bound records must belong to the current run.

The runtime maps selected records to optional `recalled_memory`
`ContextCandidate` values. Their provenance begins with
`memory:untrusted-derived:`, their priority cannot exceed zero, they are never
required, and they are not carried into a later turn automatically. They share
the same candidate and prompt budgets as game observations. A recalled record
can influence planning, but cannot settle an action, bypass a tool, or replace
an authoritative host receipt.

`SelectCommittedMutations` runs only after its inputs are durable. Its
`RuntimeMemoryCommitContext` includes:

- terminal action receipts for the turn, excluding `unknown`;
- the already committed transcript;
- the committed assistant message and optional final output;
- world, session, run, turn, and optional game-semantic coordinate;
- a stable batch `CommitId` used only for idempotent writeback;
- `CommittedSourceEventIds`, the exact durable receipt/provider/output events
  that an upsert may cite in `MemoryProvenance.SourceEventId`.

Receipts are authoritative evidence of host actions. Assistant text and final
output are derived evidence only; the game policy decides whether either should
be remembered. This hook runs for receipt turns, committed follow-up responses,
and pure-text final responses, so dialogue-only games do not need a fake memory
tool.

Every returned upsert must have committed provenance for the same world and
source run, and its source event must be one of the context's committed event
IDs. Session-bound provenance cannot point at another session. Runtime
integration further caps one policy result at 128 mutations and 512 KiB of
record content by default; configurable limits can only be raised within the
durable-event hard ceiling. The write store must implement
`IIdempotentAtomicMemoryBatchStore`.

Memory writes use a durable outbox:

1. The exact bounded mutation batch is appended as
   `memory.commit_prepared`.
2. The memory store applies the whole batch atomically under `CommitId`.
   Stores recompute the canonical payload digest: the same ID and payload is a
   durable no-op, while the same ID with another payload fails closed.
3. `memory.commit_completed` closes the outbox entry.
4. Only then may the runtime complete that turn.

A crash or cancellation after step 1 leaves a recoverable prepared batch.
Resume replays that exact batch without asking the policy to decide again, then
appends the completion marker. Exact prepared batches remain replayable after a
policy upgrade because their policy identity, payload digest, and source
evidence were already captured and validated. An empty mutation decision writes
one small `memory.commit_settled` event instead of an empty prepared/completed
pair, closing the crash window without writing a large no-op payload.

If committed source evidence exists but the process died before either a
prepared batch or settlement, resume re-evaluates only with the policy ID and
version captured in that turn's durable snapshot; a mismatch fails closed with
`memory_recovery_policy_mismatch`.

Policy exceptions, invalid mutations, incomplete strict recall, and failed
atomic writes surface as typed `RuntimeMemoryIntegrationException` reason
codes. A failed write leaves its prepared outbox entry available for a later
resume instead of pretending the memory was committed.

`WithRuntimeMemory(..., disposeOnShutdown: true)` transfers ownership of the
memory lifecycle to `BuiltGameAgentRuntime`. Shutdown first drains active agent
runs and flushes the durable run store, then waits for the memory lifecycle's
actual operation and detached-provider drain before disposing downstream
transports or the store.
`MemoryProviderCallsDrainedOnStop` reports whether detached provider calls
drained within the lifecycle's bounded timeout; `false` does not mean cleanup
was abandoned. With `disposeOnShutdown: false`, the application retains
ownership and must call `WaitForShutdownDrainAsync` after every runtime using it
has stopped and before disposing shared provider or memory-store dependencies.
Disposing the lifecycle does not dispose custom provider or memory-store
objects; their ownership remains application-defined.
