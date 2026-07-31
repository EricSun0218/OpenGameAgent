# Authoritative world execution

`GameAgent.World` separates event planning from authoritative mutation. A
`WorldEventPlan` is deterministic planning output, but it is not executable
until it is bound to the exact state and catalog that admitted it:

```csharp
var artifact = new WorldAuthoritativeEventPlan(
    plan,
    currentSnapshot.Coordinate);
var request = new WorldEventPlanExecutionRequest(
    artifact,
    hostContext);
var result = await executor.ExecuteAsync(request, cancellationToken);
```

The executable artifact carries `worldId`, `timelineId`, `timelineEpoch`,
`saveRevision`, `stateVersion`, and `catalogDigest`. The executor compares that
fence with current authoritative state before any effect is applied.
`BeginAsync` repeats the comparison under exclusive transaction ownership, so
a state change between the initial check and acquisition is rejected.

## Fixed effect registry

`WorldAuthoritativeEventPlanExecutor` resolves every event through an
`IWorldTransactionalEventEffectRegistry`. Registry keys are the fixed
`effectHandlerId` values in event definitions. The framework does not infer an
effect from narration or model text.

Each factory receives a `WorldTransactionalEffectFactoryContext` containing:

- the fixed event instance;
- the exact coordinate for this instance;
- deterministic command and operation identifiers;
- optional game-owned host context.

The coordinate advances from each committed or replayed receipt before the
next instance is prepared. Factories must only construct transaction-local
effects. External side effects require a game-owned durable outbox and must not
run during factory construction.

Execution batches run in their planned order. The baseline serializes
instances within a batch because one timeline has one authoritative writer.
The result reports every attempted instance. A rejection, cancellation, busy
result, idempotency conflict, or unknown outcome stops later batches. A
committed prefix followed by a failure is reported as partially completed,
never as whole-plan success.

## Scoped idempotency and reconciliation

Operation and command identities are isolated by:

```text
worldId + timelineId + timelineEpoch
```

`ReconcileAsync` and `CancelPendingAsync` require a
`WorldTransactionScope`; an identifier from another world, timeline, or epoch
cannot observe or cancel local work. Once an operation is pending, execution
reconciles it before considering another dispatch. A pending-unknown operation
is never automatically executed again.

## Local durable store

`FileWorldAuthoritativeTransactionStore` is the portable local-file baseline.
It implements both `IWorldAuthoritativeTransactionStore` and
`IWorldEventHistory`.

For each change it:

1. takes an operating-system file lock shared by all store instances;
2. reads and validates the current bounded snapshot;
3. writes a checksummed next image to a new file in the same directory;
4. flushes the image to disk;
5. atomically replaces the authoritative file.

`BeginAsync` persists pending ownership before returning a transaction
capability. A successful event commit publishes state, occurrence history, and
the terminal command receipt in the same replacement image. After a lost
acknowledgement, reopening the store returns either the terminal receipt or the
durable pending record; it does not guess and redispatch.

Loading fails closed for truncated or malformed JSON, duplicate properties,
invalid Unicode, unknown fields, digest mismatches, inconsistent receipts,
conflicting identifiers, and configured byte or record limits. Capacity
exhaustion never silently deletes authoritative history or receipts.

The file store is intended for a local filesystem with atomic same-directory
replacement and functioning file locks. A network share, object-backed mount,
or filesystem without those guarantees needs another implementation of the
same interfaces. A process terminated between writing and replacing can leave
one non-authoritative `.next` sibling; opening the store removes that bounded
sidecar, and the authoritative path remains the only recovery source.
