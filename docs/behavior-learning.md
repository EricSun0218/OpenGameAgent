# NPC behavior learning and self-evolution

`BehaviorLearningExtension` lets a long-lived NPC improve reusable procedures without turning the model into a policy administrator or code editor. The separately installed `SharedBehaviorCatalogExtension` lets a host publish a validated immutable procedure for discovery and lets each eligible NPC adopt it explicitly. Both are optional `OpenGameAgent.Extensions` features; nothing is added to the stable kernel loop.

## Authority model

The lifecycle is deliberately asymmetric:

1. the agent can only **propose** a procedure through `propose_behavior_learning`;
2. the injected `GameBehaviorLearningValidator` verifies game-owned evidence and rejects unverifiable claims;
3. in the default review mode, an accepted proposal remains inactive;
4. a trusted host calls `ActivateAsync` with the current session CAS revision and exact world boundary, or explicitly configures validated auto-activation;
5. only the active immutable version is projected as a normal `GameSkill` on later inputs;
6. host-recorded evaluations can demote a bad version, and an older version can be reactivated exactly.

A learned version contains instructions, a required `GameBehaviorReflection`, ordered `GameBehaviorStep` entries, input types, and names of tools it may depend on. Reflection records explicit observation, strategy, outcome, applicability, and known failure modes; it is not hidden reasoning. A step can only name one of the proposal's declared tool dependencies; it cannot register or invoke tools behind the runtime. The resulting `GameSkill` is projected only when every dependency is currently registered and visible, then tells the normal ReAct loop how to compose those tools. Policy, approval, schema validation, durable action dispatch, and game authority remain unchanged.

The host selects the learning posture through `BehaviorLearningOptions.Mode`:

- `Disabled` exposes no proposal tool, accepts no new proposals or activations, and projects no learned skill;
- `ReviewRequired` is the default: validated candidates remain inactive until an explicit `ActivateAsync` call;
- `ValidatedAutoActivate` immediately activates a candidate after the host validator accepts it and supersedes the prior active version of that behavior.

The automatic mode changes review cadence, not authority. Validation is still mandatory, and a learned version still cannot add a tool or permission. A product can tune the validator, `AllowActorScope`, evaluation threshold, and in-run policy to choose a more conservative or aggressive posture.

Do not validate evidence by asking the model whether it succeeded. Resolve references against authoritative action receipts, committed inputs, trace/evaluation records, or equivalent game-owned state. Reject transient environment failures, unresolved attempts, one-off world facts, guessed tool behavior, secrets, executable code, and any proposal that tries to broaden permissions.

## Minimal setup

```csharp
var learning = new BehaviorLearningExtension(
    boundaryProvider: (input, cancellationToken) =>
        new ValueTask<GameBehaviorWorldBoundary>(new GameBehaviorWorldBoundary(
            input.Moment.TimelineId,
            currentSaveGeneration,
            worldRevision)),
    validator: async (request, cancellationToken) =>
        await receipts.VerifyAllAsync(
            request.Input.SessionId,
            request.Input.ActorId,
            request.Proposal.Evidence,
            cancellationToken),
    options: new BehaviorLearningOptions
    {
        Mode = GameBehaviorLearningMode.ReviewRequired,
    },
    // Optional. Omit this to expose no extra tool during normal NPC runs and
    // submit isolated reviewer output with ProposeAsync instead.
    inRunPolicy: input => input.Type == "post-task-review");

await using var runtime = new GameAgentBuilder(provider, model)
    .UseSessionStore(sessionStore)
    .UseExecutionScope((input, cancellationToken) =>
        new ValueTask<GameExecutionScope>(CanLearn(input.ActorId)
            ? GameExecutionScope.Restricted(new[]
            {
                GameExecutionCapabilities.BehaviorLearning,
                GameExecutionCapabilities.PersistentPlanning,
            })
            : GameExecutionScope.ShortTaskOnly))
    .UseExtension(learning)
    .Build();
```

After the run commits, a trusted host can review and activate a proposal:

```csharp
var query = await BehaviorLearningExtension.ReadAsync(
    sessionStore,
    new GameSessionKey(sessionId, actorId),
    includeInactive: true,
    cancellationToken);

var candidate = query.Behaviors.Single(value =>
    value.Status == GameLearnedBehaviorStatus.Proposed);

var activation = await learning.ActivateAsync(
    sessionStore,
    query.Session,
    candidate.BehaviorId,
    candidate.Version,
    query.SessionRevision,
    new GameBehaviorWorldBoundary(timelineId, saveGeneration, worldRevision),
    cancellationToken);
```

The first activation fails closed if the session revision changed or if the timeline, generation, or world revision no longer matches the proposal. Review the new snapshot and revalidate instead of forcing a stale candidate active. Rolling back a previously active version still requires the same timeline and generation, and rejects a boundary whose revision predates the version's evidence, but it does not require the world to remain frozen at the original revision.

For a lower-priority post-run reviewer, construct a typed `GameBehaviorLearningProposal` and call `ProposeAsync` with the already committed source input, its current session revision, the trusted boundary, and the review run ID. This path invokes the same validator and persistence bounds without inserting the review prompt or response into the NPC transcript. An uncommitted input is rejected, and a retry for the same behavior/source input returns `AlreadyExists` instead of creating another version.

```csharp
var proposal = new GameBehaviorLearningProposal(
    "safe-resource-route",
    "Safe resource route",
    "Reuse the verified route and stop if its preconditions no longer hold.",
    GameLearnedBehaviorScope.WorldGeneration,
    new GameBehaviorReflection(
        observation: "The direct route was blocked by an authoritative hazard observation.",
        strategy: "Use the inspected alternate route.",
        outcome: "The actor reached the destination and the action receipt committed.",
        applicability: "Only while the alternate route remains traversable.",
        failureModes: new[] { "A later world update may block the alternate route." }),
    evidence: new[] { new GameBehaviorEvidence("action-receipt", operationId) },
    inputTypes: new[] { "npc.travel" },
    toolNames: new[] { "move_to" },
    steps: new[]
    {
        new GameBehaviorStep("move-alternate", "move_to", "Move through the validated alternate route."),
    });
```

## Individual learning and shared discovery

Personal versions stay in the `(sessionId, actorId)` session state. Sharing is a separate host operation; the model has no publish or adopt tool. `SharedBehaviorCatalogExtension.PublishAsync` copies one active immutable definition into `IGameSharedBehaviorStore` only after `GameSharedBehaviorPublicationValidator` approves it. The catalog supports host-defined `Game`, `WorldGeneration`, `Role`, and `Faction` audiences.

The source `BehaviorId` and version are local to one actor session. A publication therefore also requires a host-assigned, catalog-wide `BehaviorFamilyId` and monotonic `FamilyVersion`. Those two values define shared upgrades and rollbacks; they must come from the host's trusted registry, not from the NPC. The publication validator receives the publication ID, family ID, and family version so it can reject regressing or unauthorized lineage, while every built-in store atomically reserves each `(BehaviorFamilyId, FamilyVersion)` for exactly one publication and content hash. Adopting an older family version is an explicit rollback and supersedes the actor's currently active adoption in that family. Identical source-local IDs from unrelated NPCs do not collide when their family IDs differ.

Publication means **discoverable**, not active. `DiscoverAsync` returns eligible records, while `AdoptAsync` checks the current trusted boundary, audience membership, per-NPC `GameSharedBehaviorAdoptionValidator`, and session CAS before recording that actor's adoption. The extension projects only active adoptions whose publication is still published, whose content hash is unchanged, whose audience still matches, and whose required tools are currently visible.

```csharp
var shared = new SharedBehaviorCatalogExtension(
    sharedBehaviorStore,
    boundaryProvider,
    audienceProvider: (input, cancellationToken) =>
        new ValueTask<IReadOnlyList<GameSharedBehaviorAudience>>(new[]
        {
            new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.Role, GetRole(input.ActorId)),
            new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.Faction, GetFaction(input.ActorId)),
        }),
    publicationValidator: ValidateForSharingAsync,
    adoptionValidator: ValidateForActorAsync);

var publication = await shared.PublishAsync(
    sessionStore,
    sourceSession,
    behaviorId: "build-with-light",
    behaviorVersion: 4,              // source-session version
    behaviorFamilyId: "safe-house", // host catalog identity
    familyVersion: 2,                // host catalog version
    expectedSessionRevision: expectedSessionRevision,
    publicationId: "safe-house-v2-review-17",
    audience: new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.Role, "builder"),
    boundary: boundary,
    auditReference: "publication-review-17",
    cancellationToken: cancellationToken);

await using var runtime = new GameAgentBuilder(provider, model)
    .UseSessionStore(sessionStore)
    .UseExtension(learning) // optional individual learning
    .UseExtension(shared)   // optional shared catalog
    .Build();
```

Use `InMemoryGameSharedBehaviorStore` for tests or `FileGameSharedBehaviorStore` for crash-tolerant local persistence. The file store keeps crash-recoverable hash-partitioned audience indexes, hash-verifying audience manifests, and family-version reservations, so normal discovery reads only matching catalog partitions instead of deserializing unrelated publications. A family reservation is the insert linearization point: readers either see the prior state or reconcile the pending insert before exposing the new immutable publication. Missing derived audience indexes are rebuilt under the catalog lease from committed records, while malformed, same-count-tampered, or cross-audience mappings fail closed. `RevokeAsync` removes a publication from future runs and later skill selections without mutating individual NPC histories; it does not rewrite an already-issued model request. Adoption evaluation is intentionally isolated: consecutive failures suspend only that NPC's adoption. Another NPC continues using the same publication until its own evidence or a host-level revocation says otherwise. Re-adopting a suspended exact publication is an explicit rollback/retry decision; the host may also withdraw an active or suspended adoption to free its capacity slot. `MaximumAdoptionsPerActor` bounds active and suspended adoptions; `MaximumRetainedInactiveAdoptions` separately bounds withdrawn and superseded audit records without pruning active or suspended entries. `MaximumDiscoverableBehaviors` caps returned records, while `MaximumCatalogRecordsScannedPerDiscovery` counts both published and revoked records and independently bounds world-boundary filtering across paged catalog results.

## Scope, evaluation, and recovery

`WorldGeneration` is the default and recommended scope. Such a behavior is selected only while the trusted boundary provider returns the same timeline and generation. This prevents a procedure derived from a discarded save branch from silently affecting a new world.

`Actor` scope can cross world revisions and generations in the same actor session, but is disabled by default (`BehaviorLearningOptions.AllowActorScope = false`). Enable it only for genuinely general procedures independent of a save branch.

The records live in namespaced session extension state and round-trip through in-memory and file session stores. A proposal binds the framework-derived run ID, input ID, world boundary, and evidence references; the model cannot supply the run ID. Concurrent host mutations use session CAS and return a structured conflict instead of overwriting another run.

Use `RecordEvaluationAsync` with a content-free evidence reference after an offline test or authoritative gameplay observation. Success resets the consecutive-failure counter. Reaching `BehaviorLearningOptions.ConsecutiveFailuresBeforeDemotion` removes that version from subsequent model context. Hosts may also call `DemoteAsync` or `RejectAsync` directly.

Reactivating a demoted or superseded version is the rollback operation: it activates the exact stored instructions rather than synthesizing another rewrite. Rejected versions cannot be activated. Inactive audit records are pruned after `MaximumRetainedInactiveVersions`; active versions and proposals awaiting a decision are retained. `MaximumVersionsPerBehavior` is a retention bound, not a lifetime creation limit: the oldest inactive versions make room for later candidates, while a persisted session-level high-water mark prevents version reuse. Version numbers are therefore monotonic within a session but need not be contiguous for one behavior.

## Deliberate limits

- No private reasoning or hidden chain-of-thought is harvested.
- No background model call is started implicitly. A game may schedule a lower-priority reviewer and submit its typed result through `ProposeAsync`, but it must account for that model usage separately.
- No personal experience is broadcast automatically. Shared publication and per-NPC adoption are separate host-authorized operations.
- No model training, weight updates, generated executable code/tool implementation, or provider credential mutation occurs here. Learned skills are declarative procedures over tools the host already registered.
- Memory remains the place for facts, relationships, preferences, and events. Behavior learning is only for reusable procedural instructions.
