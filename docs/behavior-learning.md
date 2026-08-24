# Bounded behavior learning

`BehaviorLearningExtension` lets a long-lived NPC improve reusable procedures without turning the model into a policy administrator or code editor. It is an optional `OpenGameAgent.Extensions` feature; nothing is added to the stable kernel loop.

## Authority model

The lifecycle is deliberately asymmetric:

1. the agent can only **propose** a procedure through `propose_behavior_learning`;
2. the injected `GameBehaviorLearningValidator` verifies game-owned evidence and rejects unverifiable claims;
3. in the default review mode, an accepted proposal remains inactive;
4. a trusted host calls `ActivateAsync` with the current session CAS revision and exact world boundary, or explicitly configures validated auto-activation;
5. only the active immutable version is projected as a normal `GameSkill` on later inputs;
6. host-recorded evaluations can demote a bad version, and an older version can be reactivated exactly.

A learned version contains instructions, input types, and names of tools it may depend on. It cannot register a tool. The skill provider exposes it only when every named tool is already available after normal collection and visibility policy. Tool policy, approval, durable action dispatch, and game authority remain unchanged.

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

## Scope, evaluation, and recovery

`WorldGeneration` is the default and recommended scope. Such a behavior is selected only while the trusted boundary provider returns the same timeline and generation. This prevents a procedure derived from a discarded save branch from silently affecting a new world.

`Actor` scope can cross world revisions and generations in the same actor session, but is disabled by default (`BehaviorLearningOptions.AllowActorScope = false`). Enable it only for genuinely general procedures independent of a save branch.

The records live in namespaced session extension state and round-trip through in-memory and file session stores. A proposal binds the framework-derived run ID, input ID, world boundary, and evidence references; the model cannot supply the run ID. Concurrent host mutations use session CAS and return a structured conflict instead of overwriting another run.

Use `RecordEvaluationAsync` with a content-free evidence reference after an offline test or authoritative gameplay observation. Success resets the consecutive-failure counter. Reaching `BehaviorLearningOptions.ConsecutiveFailuresBeforeDemotion` removes that version from subsequent model context. Hosts may also call `DemoteAsync` or `RejectAsync` directly.

Reactivating a demoted or superseded version is the rollback operation: it activates the exact stored instructions rather than synthesizing another rewrite. Rejected versions cannot be activated. Inactive audit records are pruned after `MaximumRetainedInactiveVersions`; active versions and proposals awaiting a decision are retained. `MaximumVersionsPerBehavior` is a retention bound, not a lifetime creation limit: the oldest inactive versions make room for later candidates, while a persisted session-level high-water mark prevents version reuse. Version numbers are therefore monotonic within a session but need not be contiguous for one behavior.

## Deliberate limits

- No private reasoning or hidden chain-of-thought is harvested.
- No background model call is started implicitly. A game may schedule a lower-priority reviewer and submit its typed result through `ProposeAsync`, but it must account for that model usage separately.
- No shared or global skill publication is automatic. Review and publish common skills through a host-controlled package or content pipeline.
- No model training, weight updates, executable skill generation, or provider credential mutation occurs here.
- Memory remains the place for facts, relationships, preferences, and events. Behavior learning is only for reusable procedural instructions.
