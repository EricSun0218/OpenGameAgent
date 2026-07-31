# Durable agent-driven world evolution

`WorldAgentEvolutionRunner` composes simultaneous NPC decisions with the
authoritative world transaction boundary. It is engine-neutral and does not
decide game rules.

The runner:

1. checks the exact world, timeline, epoch, save, state, and catalog
   coordinate;
2. captures each participant's declared decision draft and private run-input
   digest;
3. persists the complete participant manifest before model dispatch;
4. runs participants concurrently with an aggregate budget;
5. resumes existing durable run IDs after interruption instead of dispatching
   replacements;
6. restores results in manifest order, independent of provider completion
   order;
7. calls a game-owned `IWorldAgentEvolutionReducerDescriptor`;
8. verifies that the reducer returned the captured command, operation, and
   coordinate;
9. re-reads the live world and commits one authoritative transaction; and
10. reconciles a possibly committed operation before any retry.

Models can only choose option IDs declared in each
`WorldAgentDecisionDraft`. The reducer receives those bounded proposals and
the captured authoritative snapshot. Resource arbitration, combat rules,
movement conflicts, costs, cooldowns, and all other business policy stay in
the game-owned reducer.

## Persistence

Use `InMemoryWorldAgentEvolutionStore` for tests and disposable sessions. For
restart recovery, use `JournalWorldAgentEvolutionStore` over a dedicated
`IDurableSessionStore`, such as `FileSessionStore`.

Checkpoints use compare-and-swap revisions and a bounded payload. They retain:

- the command and runtime-generation digests;
- the captured authoritative state digest;
- the canonical prepared-batch digest, aggregate reservation, and exact
  per-run digests;
- participant, incarnation-bound job, draft, run, and private-input digests;
- accepted proposal envelopes and reasons;
- reducer evidence and transaction fingerprints; and
- terminal receipt identity.

Private context content is not copied into the evolution checkpoint. Its
digest is bound to the stable run manifest; the durable agent journal remains
the source of recovery data.

An owner lease prevents normal concurrent processing. If a process stalls
past the lease, another process may take over. Long actor, reducer, world-read,
and world-transaction operations renew the lease while they are active. Every
owned write is fenced by both the random owner ID and monotonically increasing
owner generation; an expired owner cannot renew itself after the fact. A late
owner must still pass checkpoint compare-and-swap before world commit. The
authoritative world operation remains the final exactly-once boundary if an
uncooperative extension ignores cancellation during takeover.

The reducer descriptor's policy ID and digest must exactly match the command.
`IWorldAgentRuntimePolicySnapshotSource` must likewise match the command's
runtime generation and tool, skill, provider, and model policy digests. The
runner checks these bindings before first dispatch, before recovered dispatch,
and before reduction. A stale implementation pauses recovery without starting
missing runs. When using `DurableAgentRuntime`, derive those four digests from
the executable runtime instead of duplicating them:

```csharp
var policy = WorldAgentRuntimeGeneration.FromExecutionPolicy(
    hostRuntimeGeneration,
    built.Runtime.CaptureExecutionPolicyIdentity());
```

Prepared evolution runs persist that exact identity. The durable runtime
captures one tool catalog, skill catalog, and provider route plan, compares
the captured identity before provider or tool dispatch, and then reuses the
same lease for every turn in that `RunAsync` or `ResumeAsync` loop invocation.
If policy changes between the runner's preflight and an actor's admission, the
actor fails closed before gameplay side effects. Unbound generic agent runs
still observe hot reload on their next loop invocation.

## Recovery behavior

- Recovery first rebuilds and fully admits the whole prepared batch. A
  manifest with no durable participant run starts that same stable run ID only
  after its canonical request digest and aggregate reservation still match.
- An existing run is resumed with job identity and semantic guards.
- Cancellation never starts a participant whose durable run does not exist.
- Unknown runtime or storage failures leave the participant unresolved and
  return `ReconciliationRequired`; they are not converted into permanent actor
  failures.
- A terminal participant result is reused without another provider or tool
  call.
- A pending or unknown world operation returns
  `ReconciliationRequired`; it is never resent on a timeout.
- A changed state, catalog, timeline, save revision, or entity incarnation
  rejects late proposals.
- A repeated completed command returns `Replayed` with its authoritative
  receipt.

Run-input factories are capture adapters and must be side-effect-free.
Gameplay side effects belong in tools with receipts or in the single reducer
settlement transaction.

## Clock, memory, and presentation boundaries

Fixed clock events can run before this decision boundary through
`NativeWorldRuntime`. Use the resulting exact coordinate for the evolution
command. Promote the next clock boundary only after the evolution result is
terminal.

Memory, group-memory, and presentation records must be derived from a
committed world receipt. Do not write “success” presentation or memory before
the world transaction commits. Their durable outbox composition is separate
from the game-owned reducer so narrative data cannot become authoritative
game state accidentally.
