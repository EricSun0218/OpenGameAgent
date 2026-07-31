# Group interactions

`GameAgent.Core` includes a bounded, engine-neutral shared-interaction
primitive. It is intended for scenes in which several characters can observe
or contribute to one interaction while retaining independent identity,
context, and memory.

The primitive is not limited to chat. A message payload is JSON and can
represent text, a menu selection, a world event, a tool result, a negotiation
proposal, or another game-defined value.

## Guarantees

- A session has a stable session ID, group ID, revision, membership revision,
  status, and explicit shared-scope JSON.
- A session may carry a `GroupInteractionWorldBinding` with its exact world,
  timeline, timeline epoch, and creation/rebind save revision. The binding is
  part of create-operation evidence, so durable restore detects tampering.
- Membership binds an entity ID to an exact incarnation. Reusing an entity ID
  after despawn, respawn, possession, or reincarnation does not inherit
  directed history.
- Every committed message records its exact audience at commit time.
  `all_members` resolves to the current membership snapshot; `explicit`
  requires a non-empty subset of exact current members.
- A batch of messages is appended atomically in caller order. Model completion
  order never becomes transcript order by accident.
- Writes use revision and membership-revision compare-and-swap fences.
- Operation IDs and canonical request digests make retries idempotent and make
  conflicting reuse fail closed.
- Ordinary writes cannot consume the final operation slot. That slot is
  reserved for a durable close transition, so reaching capacity never leaves
  an open session that cannot be terminated.
- Member, message, operation, JSON depth/node, per-payload, and aggregate byte
  limits are enforced before state changes.
- A participant projection contains only messages addressed to that exact
  entity incarnation.

Private memory is never copied into a group transcript automatically. A game
must deliberately project information into a message payload and audience.

## Storage

`InMemoryGroupInteractionStore` is an atomic reference implementation for
embedded sessions and tests. `FileGroupInteractionStore` is the durable local
implementation. It appends checksummed immutable snapshots, verifies the
digest chain on reopen, rejects an invalid committed frame, ignores a torn
uncommitted tail, enforces log/session/frame capacities, and uses the same
revision and operation-id semantics as the in-memory store. Use one file per
independent durability boundary; opening the same canonical path twice in one
process is rejected.

A game can also implement `IGroupInteractionStore` with its own durable
transaction, or persist the immutable `GroupInteractionSession` as part of
authoritative world state. After decoding persisted fields, pass them through
`GroupInteractionStateMachine.Restore`; it remeasures JSON, verifies digests,
requires contiguous operation/revision history, and reapplies configured
capacity limits. Persist `MembershipHistory`, each message's audience mode and
applied revision, and each operation's kind together with the rest of the
session. Restore uses that evidence to reconstruct every request digest and
prove that historical authors and audiences were exact members when their
messages committed.

The generic stores continue to admit legacy sessions without a world binding
so applications can open and migrate existing files. Authoritative settlement
delivery and complete interactive-world bundle capture fail closed for such a
session: they never infer a world scope from a file path, session ID, member
ID, or caller promise. New world-backed sessions should pass
`GroupInteractionWorldBinding` to `GroupInteractionCreateRequest`.
`GroupInteractionStateMachine.RebindWorld` is the explicit derivation used
when a complete bundle forks to a new timeline; it preserves the transcript
and revision history while recomputing the bound create-operation evidence.

`GroupInteractionWriteResult` has a public validated constructor so a store in
another assembly can report all interface-defined outcomes. Successful writes
must include their applied revision; rejected writes carry the current session,
except `not_found`, which carries no session.

Do not acknowledge an applied write before the state and its operation record
are durable together. On retry, compare both operation ID and request digest.
The file store does this before returning `applied`; cancellation during a
mutation either leaves no committed frame or leaves a frame that is recovered
idempotently.

## Game-owned policy

The framework deliberately does not decide:

- who is present or allowed to speak;
- speaker order, interruption, mentions, or automatic continuation count;
- whether an interaction is face-to-face, remote, private, or public;
- which world facts a participant is allowed to observe;
- whether a proposal changes the world;
- how a visible message becomes a character memory.

Those decisions depend on game rules. The framework supplies the stable
identity, visibility, concurrency, size, and retry boundaries needed to
implement them safely.

## Minimal flow

1. Create a session with explicit shared scope and exact members.
2. Project the session separately for each participant.
3. Build one agent request per selected speaker from that participant's
   projection and private context.
4. Run speakers sequentially or with `MultiActorDecisionCoordinator`.
5. Validate any proposed game action through the authoritative host boundary.
6. Append only the game-approved shared result with an explicit audience.
7. Persist private memories separately under each participant's perspective.

Membership changes increment `MembershipRevision`. A run created before that
change must not append against the new membership snapshot; it must be
discarded or rebuilt with current context.
