# Interactive world v1

This document defines the first interactive-world layer built on top of the
agent runtime. It supports games that opt into explicit, discrete evolution
boundaries, such as advancing one game-defined turn, day, or month. Fixed event
definitions drive that evolution. An agent is called only when a step needs
language understanding, a choice among legal options, or presentation text.

This is the implemented v1 contract. The engine-neutral package compiler,
declarative evaluator, authoritative transaction stores, save bridge, and
engine sessions ship in `GameAgent.World`; compatibility importers ship in
`GameAgent.Compatibility`.

This project ships framework primitives, authoring tools, validators,
diagnostics, and conformance examples. It does not ship a built-in game model.
Character progression, travel, social relationships, combat, economy, quests,
and every other domain rule belong to packages or game-owned extensions.
Advancing a month is only one possible game-defined periodic trigger; the core
understands named discrete ticks, not months.

## Product boundary

The first phase contains:

- an engine-neutral native world-package format;
- a separate, resumable save format;
- loss-reporting import adapters for character and world-book content;
- a deterministic evaluator for fixed triggers, conditions, effects, choices,
  schedules, and bounded event cascades;
- optional agent escalation for understanding, selection, and narration;
- a Godot-first editor and runtime integration;
- a small conformance sample that can import content, advance a clock, resolve
  an event, save, load, and export the native world. It is not an actual game.

It is not a continuous autonomous simulation and does not call one model per
NPC per frame. It does not make imported prose executable, infer game rules
from lore, or turn model output directly into authoritative state.

Fast setup comes from declarative definitions, standard safe handlers, import
adapters, editor validation, event inspection, replay, and debugging. It does
not come from hard-coded gameplay.

The first phase is single-player. The data and event contracts still use
explicit world, timeline, entity-incarnation, state-version, and operation
identities so a later authoritative multiplayer host does not require a new
content format.

## Layering

```text
Native package and imported authored content
                    |
                    v
       Interactive-world evaluator
  triggers, conditions, draft state, choices
                    |
          +---------+---------+
          |                   |
          v                   v
 Fixed declarative effects   Agent runtime
 and game-owned resolvers    tools, skills, memory
          |                   |
          +---------+---------+
                    v
        Engine host and game presentation
```

The interactive-world evaluator is part of the game layer. It operates through
a game-owned state adapter and produces typed effect intents for handlers to
validate and apply. A small project may use a standard safe JSON-state handler;
a larger project can map the same contracts to its own storage. Neither path
supplies domain rules. The agent runtime remains below this layer and never
decides game legality.

The existing runtime primitives retain their current responsibilities:

- `GameContextCoordinate` fences one world/timeline/save/state snapshot and
  carries game time, causality, observer identity, and incarnation.
- `GameContextReceiptEnvelope` advances that coordinate only from
  authoritative terminal action receipts.
- `MultiActorDecisionCoordinator` runs bounded, isolated actor decisions
  against one immutable coordinate.
- the game host validates and commits mutations and resolves simultaneous
  action conflicts.

The world layer adds package ownership, discrete trigger evaluation, a draft
transaction, event ordering, and import/export. It does not duplicate provider
recovery, tool execution, memory, or multi-actor scheduling.

## Native artifacts

V1 defines four logical JSON contracts:

| Contract | Purpose |
| --- | --- |
| `game-agent.world-package.v1` | Immutable authored definitions and asset references |
| `game-agent.world-save.v1` | One world instance, timeline, state, schedules, and evolution cursor |
| `game-agent.world-command.v1` | Typed input that starts or continues world work |
| `game-agent.world-event-log.v1` | Ordered evidence for committed evolution |

The native source files are `world.json` plus optional `clocks.json`,
`numerics.json`, `events.json`, `interactions.json`, `agents.json`, and
`knowledge.json`. Their schemas live under `schemas/world-v1`. A deterministic
`.gaworld` archive carries the compiled source package. Contract identity is
also carried inside each root object and is never inferred only from a
filename.

### Package contents

A native package contains:

- a manifest with package ID, content version, format version, file digests,
  dependencies, and required extension capabilities;
- entity and actor templates;
- component, relationship, and location definitions;
- clocks and optional calendar labels;
- portable numeric schemas and interaction definitions;
- fixed event definitions;
- knowledge entries and agent-profile data;
- declarative skills that require no executable code;
- presentation templates and content-addressed assets;
- namespaced extension data that the manifest declares.

A package does not contain:

- a live world instance or save history;
- provider credentials, endpoint secrets, billing data, or local configuration;
- an agent journal, model continuation state, cache, or telemetry;
- executable assemblies, native libraries, scripts, or engine plugins;
- silently retained source imports.

Every file listed by the manifest has a SHA-256 digest. JSON rejects duplicate
properties, invalid Unicode, excessive nesting, and unknown unnamespaced
fields. Logical asset URIs are authoritative; engine resource IDs and scene
paths are generated caches.

Native import followed by native export must preserve all native semantics.
Valid namespaced extension values are preserved. A reader rejects an unknown
required extension instead of silently deleting it.

### Save contents

A save references an exact package ID, content version, and package digest. It
contains:

- `worldId`, `timelineId`, parent-timeline metadata, and save revision;
- the current clock coordinates and epochs;
- entity instances and incarnation counters;
- component, relationship, location, and game-defined extension state;
- pending schedules and committed occurrence identities;
- accepted player and agent choices needed for deterministic replay;
- the ordered event log or a checkpoint plus its verified trailing log;
- memory references and game-owned memory policy state;
- an optional pending evolution transaction with draft state and cursor.

Package definitions and mutable save state remain logically separate even when
an export command places both archives in one outer bundle. Loading a save
requires an exact package digest or an explicit, deterministic migration.

Exporting a pending save is allowed only when the complete draft, event queue,
budget counters, pending choice or durable agent-run identity, and base-state
digest are present. Otherwise export waits for settlement or fails with a
diagnostic.

### Trusted extensions

Executable capability is distributed and installed separately from a world
package. A package can declare a required capability ID and version range, but
it cannot embed or activate the implementation.

A trusted extension may register:

- game-specific condition or effect operators;
- tool handlers and skill-admission policy;
- import adapters;
- deterministic migrations;
- conflict resolvers;
- engine-specific presentation bridges.

The host presents the requested capabilities before opening a package. It
records the approved extension identities in the save. Missing, changed, or
unapproved capabilities fail closed before event evaluation.

Declarative skill text may live in a data package, but it gains no tool
permission by being present. Normal runtime skill admission and exact tool
version checks still apply.

## Optional world primitives

Packages can opt into a small set of open schemas and primitives instead of an
RPG-specific object model:

| Primitive | Required meaning |
| --- | --- |
| Entity | Stable ID, kind, incarnation, tags, components, and optional location |
| Component | Typed JSON state with a declared schema and version |
| Relationship | Typed, directed edge between entity incarnations |
| Location | Entity or node in game-defined containment and topology |
| Clock | Named discrete tick plus timeline and epoch |
| Knowledge entry | Authored context with scope, activation hints, and provenance |
| Agent profile | Persona/context sources, permitted skills, tool policy, and budgets |
| Numeric schema | Portable integer or scaled fixed-point field contract |
| Interaction definition | Typed actor/target operation compiled into an event |
| Event definition | Versioned trigger, selector, condition, and ordered steps |
| Event occurrence | One immutable expansion of a definition for a trigger and subject |
| Schedule | A future trigger bound to a clock coordinate |
| Choice | A bounded set of game-validated option IDs |
| Presentation record | Non-authoritative text, localization key, or media cue |

These are interoperability shapes and evaluator inputs, not a world that the
framework implements for the developer. A package uses only the primitives it
needs. Games define component schemas, entity kinds, relationship types,
numeric fields, interactions, handlers, resolvers, and effect operators through
namespaced data or trusted extensions. The base format does not assume health,
affinity, money, attributes, combat, inventory, quests, factions, dialogue, or
any other game field.

Knowledge entries are context, not automatically authoritative world facts.
They may describe public lore, private belief, rumor, falsehood, or presentation
guidance. Perspective and incarnation scope use the same isolation rules as
runtime memory.

Relationships are directional. If a game needs an inverse or symmetric view,
it declares and updates that view explicitly or installs a deterministic
resolver. The framework never infers that one relationship edge implies
another.

## Typed commands

Natural language is optional. Every interaction enters the world layer as a
typed command with:

- a unique `commandId`;
- `worldId` and `timelineId`;
- expected save revision and state version;
- a command kind;
- bounded JSON payload;
- optional actor and entity-incarnation identity;
- an idempotency key when the command may cause effects.

The initial command kinds are:

| Kind | Effect |
| --- | --- |
| `advance_clock` | Advance one named clock by a positive number of ticks |
| `emit_event` | Admit a game-owned typed trigger |
| `query_interactions` | Read the bounded interactions visible and available to an actor |
| `execute_interaction` | Admit one typed interaction into the event transaction |
| `submit_choice` | Resolve one pending choice with an offered option ID |
| `submit_input` | Supply typed or textual data to a waiting understanding step |
| `resume_evolution` | Continue a budget-paused or recovered transaction |
| `cancel_evolution` | Discard the draft and leave committed state unchanged |

An engine button, menu selection, simulation signal, numeric payload, or
network message can create the same command without producing prose.

Mutating commands use optimistic admission. The expected coordinate must match
exactly, and only one evolution transaction may own a world/timeline at a time.
Retrying the same `commandId` returns the same durable outcome. A different
command with a stale revision is rejected before planning. A read-only
interaction query does not acquire evolution ownership; it reads one exact
committed coordinate and becomes stale as soon as that coordinate changes.

## Interactions

`InteractionDefinition` is the optional, declarative entry point for direct
world interaction. It does not create a second execution system. A successful
`execute_interaction` command compiles to a root event occurrence and uses the
same draft, conditions, handlers, ordering, budgets, receipts, idempotency,
conflict resolution, recovery, and event log as every other trigger.

An interaction definition contains:

- a stable interaction ID and version;
- an actor selector and required actor-incarnation policy;
- an optional target selector and target schema;
- a closed JSON parameter schema;
- supported game-defined interaction channels and capability tags;
- visibility and availability conditions;
- typed costs and their failure reason codes;
- cooldown clock, duration, and key when applicable;
- game-time duration or completion schedule when applicable;
- ordered event steps;
- optional understanding, selection, or narration jobs with explicit fallback;
- declared read and write paths;
- presentation and localization metadata.

No field above has built-in game meaning. A cost can reference any
package-defined portable numeric field. A duration is a count on a named
game-defined clock, not wall time. A cooldown key and subject identities
determine scope; the framework does not assume that cooldowns belong to an
actor, target, item, or location.

Channel IDs allow a game to distinguish interactions performed in the current
scene from those performed through a remote game-defined channel. Availability
can inspect channel, location, reachability, permissions, and capability tags.
The framework filters on declared predicates but does not define distance,
communication, presence, or access rules.

### Available-interaction query

`query_interactions` is a bounded, read-only projection. Its request supplies:

- the exact world, timeline, save revision, and state version;
- actor identity and incarnation;
- optional target identity and typed query context;
- interaction channel and game-defined reachability context;
- optional definition namespace or tag filters;
- result limit and deterministic continuation cursor.

The result is ordered by interaction ID and version and can include:

- interaction and presentation identity;
- closed parameter and target schemas;
- availability state and a stable game-defined reason code;
- evaluated cost, cooldown, and duration data permitted by visibility policy;
- the source state version, catalog digest, and availability-evidence digest.

Games may omit hidden or unavailable definitions rather than revealing their
reason. A query never reserves resources and does not promise that an
interaction will still be available later.

### Typed execution

`execute_interaction` supplies the exact interaction ID and version, actor and
target incarnations, schema-valid parameters, expected state version, and an
idempotency key. It also supplies the catalog digest displayed during player
confirmation. An availability-evidence digest may be returned for diagnostics
but never replaces fresh validation.

The evaluator rechecks visibility, availability, costs, cooldown, target
incarnation, permissions, and parameter schema against the transaction's exact
source coordinate. A changed catalog produces a stable stale-catalog result so
the UI can query and confirm again. The evaluator then derives the root
occurrence ID and compiles the definition's steps. Cost consumption, cooldown
creation, scheduled completion, and other state changes are ordinary draft
effects and commit atomically with the interaction.

The intended player flow is query, present, confirm, and execute. Confirmation
does not reserve state. Execution always revalidates the actor, target,
channel, parameters, cost, cooldown, permissions, and state version.

An interaction can use an agent only through the three escalation jobs defined
below. The agent may understand bounded input, select a declared legal option,
or narrate the result. It cannot invent a new interaction, parameter, target,
cost, cooldown, duration, handler, or effect.

## Portable numeric state

Authoritative package and save numbers use one portable schema family:

- signed 64-bit integer with scale `0`;
- signed 64-bit scaled fixed-point with a declared scale.

Each numeric field declares:

- numeric kind and scale from `0` through `18`;
- inclusive minimum and maximum;
- default value;
- a stable game-defined unit ID;
- overflow and rounding policy where an operation can require rounding.

Stored values are canonical signed base-10 strings containing the unscaled
Int64 value. A fixed-point value is `unscaled / 10^scale`. Leading plus signs,
unnecessary leading zeroes, negative zero, exponent notation, `NaN`, and
infinities are rejected. Bounds and defaults use the same canonical unscaled
representation.

The evaluator provides checked, deterministic operators:

- exact comparison;
- add and subtract for compatible scale and unit;
- multiply and divide with an explicit result schema and rounding mode;
- clamp to declared bounds;
- atomic non-negative consume with an `insufficient` result;
- explicit rescale;
- pure derived expressions over bounded numeric paths.

All intermediates are evaluated exactly and must fit the declared result
Int64 after explicit rounding. Portable rounding modes are
`reject_if_inexact`, `toward_zero`, `floor`, `ceiling`, and `half_even`.
Overflow, division by zero, incompatible units, scale mismatch, missing values,
and out-of-bounds results fail with stable reason codes. They never wrap,
saturate implicitly, or fall back to binary floating point.

Derived expressions use a closed, bounded, acyclic JSON expression tree. They
can read declared numeric paths and constants, apply the safe operators, and
produce one declared result schema. They cannot call a model, read wall time,
use engine state, mutate a value, or execute an extension.

Binary floating-point values are allowed only in explicitly non-authoritative,
engine-local extensions for rendering, animation, audio, physics, or other
presentation work. They cannot affect package conditions, authoritative
component state, interaction availability, costs, cooldowns, event ordering,
entropy, occurrence IDs, saves, conflicts, or deterministic replay. A game
that admits an engine float into authoritative input must first quantize it to
a declared portable schema with an explicit rounding policy.

## Catalog snapshots and hot updates

Event, interaction, handler, resolver, tool, and skill catalogs have stable
entry IDs, versions, content revisions, and digests. The world layer captures
the exact effective catalog snapshot before planning an evolution transaction.
Every occurrence and interaction receipt binds the relevant entry versions and
snapshot digest.

Development-time hot reload publishes a new immutable catalog generation. It
never mutates the snapshot of an active transaction, pending choice, or
in-flight actor batch. New work observes the new generation only after the host
atomically activates it. A player-facing query binds its catalog digest so
confirmation fails visibly instead of executing a different definition after a
hot update.

Production package changes also change the package digest. A persisted save
must run an explicit migration before it can activate that package revision.
The runtime tool, skill, and provider-route snapshots stay fixed for one
RunAsync or ResumeAsync agent-loop invocation; the world snapshot records
which generation each agent job was prepared to use.

## Fixed event definitions

An event definition contains:

- a stable definition ID and version;
- one trigger;
- an optional subject selector;
- a declarative condition;
- integer priority and phase;
- ordered steps;
- an occurrence-key declaration;
- optional semantic limits lower than the host hard limits;
- a declared failure or fallback policy for every agent-dependent step.

### Triggers

V1 supports explicit triggers:

- a named clock boundary or interval;
- a typed world command;
- a named event emitted by another occurrence;
- a declared component-path change;
- a schedule becoming due.

There is no implicit scan for every condition after every mutation. The
evaluator indexes definitions by trigger, and state-change triggers receive the
exact changed paths emitted by a committed draft effect. This keeps work
observable and bounded.

If `advance_clock` crosses several boundaries, the evaluator processes them in
ascending tick order. It completely settles one boundary before planning the
next. Calendar labels such as "month" are package-defined views over a numeric
clock and do not change ordering semantics.

### Selectors and conditions

Selectors expand a trigger into zero or more subjects. Entity and relationship
results are sorted by stable ordinal IDs before occurrence identities are
derived. Every selector declares or inherits a maximum candidate count.

The base condition language is closed and declarative:

- `all`, `any`, and `not`;
- `exists`;
- `eq`, `neq`, `lt`, `lte`, `gt`, and `gte`;
- bounded collection membership;
- entity tag and relationship predicates;
- comparison with trigger payload, clock, or component paths.

Paths resolve only under declared roots such as `world`, `subject`,
`relationship`, `clock`, and `trigger`. Missing values have an explicit
`missing` result and are never coerced to zero, false, or an empty string.
Conditions cannot call a model, execute code, read wall time, access the
network, or enumerate an unbounded collection.

Conditions are evaluated when an occurrence reaches the front of the queue,
against the current draft. Earlier occurrences in the same boundary can
therefore change a later condition in a deterministic way.

### Deterministic entropy

Chance and weighted selection use a named deterministic entropy contract, not
an engine random generator or queue-consumption order. The V1 portable profile
derives bytes from SHA-256 over the canonical JSON encoding of this array:

```json
[
  "entropy-version",
  "world-seed",
  "timeline-id",
  "occurrence-id",
  "roll-key"
]
```

Each roll key is unique inside its occurrence. The entropy version and world
seed are saved. Reordering independent events does not consume or shift a
shared random stream.

### Declarative effects

The safe data profile can stage:

- set, bounded append-unique, and remove operations on declared component
  paths;
- the checked numeric add, subtract, multiply, divide, clamp, consume, and
  rescale operations defined above;
- all-or-nothing multi-path patch batches;
- atomic transfer between two entity-incarnation paths, with matched debit and
  credit or no change;
- entity creation and retirement;
- directional relationship creation, update, and removal;
- movement between declared locations;
- schedule creation and cancellation;
- a named event emission with bounded typed payload;
- creation of a player or agent choice;
- a presentation record.

Each effect declares its read and write paths. Schema validation, target
incarnation, permissions, preconditions, and state-version checks run before
the effect enters the draft. Entity ID reuse requires a new incarnation.
Every operation in a multi-path patch or transfer validates before any member
is staged. A later boundary failure discards the complete draft, including both
sides of a transfer.

Effects cannot write engine objects, files, network services, provider state,
or credentials. A trusted effect operator can propose additional game-owned
work, but V1 evolution remains atomic only if that work is confined to the
draft. Irreversible external effects are deferred until after world commit or
handled by a separate game-owned transaction protocol.

### Handlers and resolvers

The framework supplies standard safe handlers for the declarative effects
above. A handler receives a validated intent, exact draft coordinate,
idempotent operation ID, and bounded cancellation token. It returns a typed
receipt; it cannot report success without the corresponding validated draft
result or a declared no-op.

A resolver receives the complete bounded proposal set, source state version,
declared read/write sets, and stable ordering keys. It returns accepted,
rejected, or reduced intents plus reason codes. Resolver identity and version
are captured in event evidence. A package can use only the portable base
reducers or require an explicitly approved game-owned resolver.

Handlers and resolvers never receive provider credentials. Model output reaches
them only after schema, option, occurrence, incarnation, and state-version
validation.

Periodic triggers and interactions compile to the same typed effect-request and
effect-receipt contracts. There is no clock-specific or interaction-specific
mutation API. When an agent invokes a game tool, the same intent travels through
the runtime's durable `ActionRequest` and authoritative `ActionReceipt`
boundary.

The evaluator writes the request before dispatch. A terminal authoritative
receipt can stage the validated result and continue planning. An `unknown`
receipt moves the evolution transaction to reconciliation and records the
operation ID. Recovery queries that operation; it never dispatches the effect
again. A rejected or failed receipt becomes typed event evidence and follows
the definition's declared failure policy.

## Evolution transaction

An `advance_clock` command uses a copy-on-write draft:

```text
admit command
  -> create draft from exact committed revision
  -> plan next clock boundary
  -> evaluate ordered occurrence queue
  -> wait for required input or durable agent result when necessary
  -> validate draft and event evidence
  -> atomically promote state, clock, log, and command receipt
```

Committed state remains readable while evolution is waiting. Mutating gameplay
commands for that world/timeline are rejected until the transaction commits or
is cancelled. Debug UI may inspect the draft but must label it as pending.

The pending transaction is durable. A process restart resumes from the last
committed cursor and never repeats an accepted choice, completed agent
decision, or applied draft operation. Cancellation discards the draft and
leaves the committed clock and state unchanged.

If an agent job is in flight, cancellation first durably requests cancellation
and fences that job's identity. The transaction remains `cancelling` until the
agent runtime settles or proves that a late result cannot be adopted. Only then
may the draft be discarded. A provider result that arrives after the fence
cannot reopen or mutate the cancelled transaction.

Each crossed clock boundary is one atomic promotion. A command that advances
twelve ticks may therefore produce twelve committed revisions. Failure at tick
seven leaves the first six boundaries committed and the seventh boundary
uncommitted. The durable command outcome reports the exact completed and
remaining range.

### Occurrence identity and idempotency

An occurrence ID is derived from canonical bounded data:

- world and timeline IDs;
- trigger instance or schedule ID;
- clock, epoch, and tick when applicable;
- event definition ID and version;
- ordered subject identities and incarnations;
- parent occurrence ID and child ordinal for cascades.

The portable ID is the lowercase hexadecimal SHA-256 digest of the canonical
JSON array of those fields. Every step operation ID derives from the occurrence
ID and step index. The draft ledger stores admitted occurrence and operation
IDs. Duplicate IDs return their recorded result; they do not reapply effects or
call an agent.

Manual triggers include `commandId` in their trigger identity. Scheduled
triggers use the stable schedule ID. Clock triggers use their exact coordinate,
so retrying a clock command cannot create a second monthly occurrence.

### Queue ordering

The evaluator uses one stable key:

1. clock tick, ascending;
2. cascade depth, ascending;
3. phase, ascending;
4. priority, descending;
5. event definition ID, ordinal ascending;
6. ordered subject identity tuple, ordinal ascending;
7. occurrence ID, ordinal ascending.

An emitted child has its parent's depth plus one and cannot preempt shallower
work. Newly emitted events enter the same queue and follow the same ordering.
Provider completion order, thread scheduling, dictionary order, asset order,
and localized display names never affect simulation order.

### Cascades and budgets

Every evaluator has explicit hard limits for:

- boundaries per command;
- selector candidates per trigger;
- occurrences per boundary and command;
- cascade depth;
- children emitted by one occurrence;
- component patch operations and changed paths;
- pending choices;
- agent decisions;
- draft JSON bytes, nodes, depth, and collection sizes;
- event-log and pending-transaction bytes.

Packages may lower limits but cannot raise the host hard caps. The effective
semantic limit set is captured in the pending transaction and event evidence.
Changing it during resume is rejected.

Crossing a semantic hard limit faults the current boundary and discards that
boundary's draft. It never truncates the queue and pretends evolution
completed. Operational provider, token, cost, and duration budgets can instead
pause the transaction. Resume retains the same semantic limit set and
occurrence identities.

## Agent escalation

Fixed rules run without a model. An event can request an agent only for one of
three bounded jobs:

| Job | Allowed result |
| --- | --- |
| Understanding | A value conforming to a declared closed JSON schema |
| Selection | One or more IDs from an offered, game-validated option set |
| Narration | Non-authoritative presentation content |

Understanding and selection use strict final-output admission. Their contract,
option IDs, event occurrence, subject incarnation, draft state version, and
game coordinate are captured before dispatch. The model cannot add an option,
change an effect, or bypass a precondition.

Each required step declares one deterministic failure policy:

- pause for explicit player input;
- choose a declared fallback option;
- skip the occurrence with an auditable reason;
- fault and discard the current boundary draft.

There is no implicit "best effort" fallback.

Narration runs after the corresponding world state commits whenever possible.
Its output is stored as a presentation record, not as proof of a fact or
mutation. Narration failure never rolls back committed world state and can be
retried by presentation job ID.

### Multiple actors

Independent actor selections can use `MultiActorDecisionCoordinator` against
one immutable draft coordinate. The world layer:

1. selects participants in stable entity-ID order;
2. creates unique decision keys from occurrence and actor identities;
3. gives each actor private context and memory scope;
4. collects typed proposals without applying them;
5. sorts accepted results by the event ordering key;
6. resolves write-set conflicts before staging effects.

Concurrent model work does not imply concurrent mutation. Overlapping writes
fail closed unless the definition declares a portable commutative reducer such
as numeric sum, minimum, maximum, or set union, or a trusted game resolver
handles the complete conflict set. A resolver receives all proposals and the
same source state version.

A periodic actor batch persists its complete participant manifest and one
status per participant. Completed, failed, paused, and abandoned participants
can coexist while the boundary is pending. Completed structured results are
durable and are not regenerated; nonterminal participants resume by their
durable run identities. The world draft does not promote until the definition's
settlement policy and conflict resolver have observed the complete required
set.

Every result carries batch, participant, occurrence, catalog, and source-state
identity. A result that arrives after cancellation, draft replacement, catalog
identity mismatch, or state-version change cannot enter the active batch. A
newer globally published catalog does not invalidate an active batch's captured
snapshot. If a durable operation may have reached the host, it is reconciled by
operation ID; it is never executed again. A command spanning several clock
boundaries reports both the committed boundary prefix and the pending or failed
remainder.

Actor context and memory never default to one shared transcript. Private scope
binds actor identity plus incarnation. Group scope binds a stable group ID,
membership revision, and timeline; only explicitly shared records enter that
scope. Public/world scope is separate. Changing group membership or reusing an
actor ID does not expose prior private or group records without an explicit
game-owned migration.

## Persistent presentation

Committed dialogue, event outcomes, and narration are stored as presentation
records with stable record ID, source occurrence or interaction ID, timeline,
audience scope, content revision, ordering sequence, and provenance. Reloading
a save presents those records again without calling a model.

Presentation is not authoritative simulation state, but it is durable user
experience. A correction or regeneration creates a new revision linked to the
old record; it does not silently replace history. Private, group, and public
audiences use the same incarnation and membership isolation described above.
Late narration for a cancelled or superseded occurrence is fenced and cannot
appear in the active timeline.

## Timeline and replay semantics

Package definitions are immutable input to one evaluation. A package update
requires a new content digest and an explicit save migration before more
commands run.

Loading an older committed save is safe, but creating new history after a
previous future exists creates a new `timelineId`. A fork records:

- parent timeline ID;
- parent save revision and game-time coordinate;
- package and extension digests;
- a new timeline ID and epoch policy.

Occurrence, schedule, memory, and entity-incarnation scope includes the
timeline. Events or private knowledge from an abandoned future cannot reappear
because a tick number was reused.

Deterministic replay uses the exact package digest, trusted-extension set,
commands, accepted choices, structured agent results, and event evidence. It
does not call a provider again. Re-running a model is an evaluation mode and
creates new evidence rather than proving the old result.

## Character and world-book import

External formats are import sources, not the native interchange format.
Importers are versioned adapters and run as untrusted data parsers.
The phase-one compatibility profile accepts bounded UTF-8 JSON character
documents, PNG character cards with embedded bounded JSON metadata, and UTF-8
JSON world-book documents. Each adapter declares the exact source schema
versions it accepts.

### Character content

Recognized character fields map into:

- an entity or actor template;
- display and localization metadata;
- an agent profile containing persona, description, scenario, and authored
  conversational examples;
- optional initial greeting or presentation seeds;
- embedded knowledge entries.

Imported persona and example text is authored context. It is never treated as
runtime policy, a tool declaration, a capability grant, or executable
instruction. Source extensions that have no safe mapping produce diagnostics.
Raw source bytes are not retained unless the developer explicitly requests an
opaque, non-executable archival attachment.

### World-book content

Recognized entries map into scoped `KnowledgeEntry` values:

- constant entries and bounded literal primary/secondary keys can produce
  scoped memory through an explicit host-projected game context;
- content retains authored provenance;
- enabled state, order, priority, and scope map where the
  native contract can express them;
- unsupported source semantics remain metadata and produce stable
  diagnostics.

A world-book activation hint controls retrieval only. It is not an event
trigger and cannot mutate world state. Imported lore becomes an authoritative
fact only through an explicit developer mapping.

Phase one evaluates constant entries plus literal primary and secondary keys.
It defines ordinal case matching, Unicode whole-word boundaries, newest-first
scan depth, and all four secondary-key logic modes. The search projection is
bound to an opaque game-time coordinate rather than wall-clock time or chat
turns. Regular-expression keys, probability, sticky/cooldown/delay state, and
recursive scanning fail closed or are skipped with deterministic diagnostics;
they never appear active merely because their metadata was imported. See
[imported character and lore activation](imported-content-activation.md).

### Import diagnostics

Import is transactional and returns a report with:

- adapter ID and version;
- source digest without source secrets or local paths;
- stable diagnostic code and severity;
- source pointer and proposed native target;
- transformation performed;
- loss, unsupported feature, collision, truncation, or security reason;
- generated native IDs and namespace mapping.

Errors make no package changes. Warnings require explicit acceptance before a
package is saved. ID collisions never overwrite existing definitions
implicitly. Export targets only the native format; compatibility export is not
a V1 goal.

## Security boundary

All packages, saves, imports, trigger payloads, and model results are untrusted
input. Implementations must:

- stream archive extraction with compressed and expanded byte, file-count,
  path-length, JSON-node, depth, and collection limits;
- reject absolute paths, parent traversal, symlinks, device names, duplicate
  normalized paths, and case-folding collisions;
- verify manifest membership and every file digest before activation;
- disable remote asset fetch by default and require an explicit allowlist when
  enabled;
- keep imported text in provenance-labeled content sections;
- prevent package data from granting tools, skills, extensions, filesystem,
  network, reflection, or process execution;
- validate every choice and agent result against the captured definition and
  state version;
- keep provider credentials and local machine paths out of exports and
  diagnostics;
- sanitize presentation markup in the engine UI;
- require explicit approval and exact version identity for trusted extensions.

A save may contain player text, model output, private NPC memory, or hidden
world state. Export UI must identify that privacy boundary and offer a
game-owned redaction policy. Redaction cannot produce a resumable save unless
the removed fields are declared presentation-only.

## Engine portability

The package, save, command, event, and deterministic entropy contracts contain
no Godot, Unity, or Unreal types. The evaluator core targets the same portable
.NET boundary as the runtime where possible.

The Godot-first integration provides:

- package and save import/export entry points;
- a dock or inspector for validation and import diagnostics;
- background evolution scheduling without blocking the scene thread;
- main-thread application of presentation records;
- generated engine-resource caches that can be rebuilt from native data;
- a conformance scene demonstrating import, interaction, one clock advance,
  multi-NPC selection, save/load, and export without defining game content.

Unity and Unreal integrations must consume the same native artifacts and
conformance fixtures. Engine adapters may add asset variants and editor UX,
but they cannot change occurrence IDs, ordering, entropy, condition results, or
save semantics.

## Phase-one conformance scenarios

The first implementation is not complete until automated fixtures prove:

1. importing one character and one world book produces a native package plus a
   deterministic diagnostic report;
2. native package export/import preserves its semantic digest;
3. advancing one month triggers a fixed event without calling a model;
4. an event with a legal-option choice invokes strict selection and applies
   only the selected declared effect;
5. typed numeric or enum input resolves an event without natural language;
6. a cascade has stable ordering and fails closed at its hard bound;
7. retrying a command, occurrence, or agent result applies no duplicate effect;
8. two NPCs decide concurrently with private context and deterministic
   conflict resolution;
9. process recovery resumes a pending draft without repeating an accepted
   choice or host action;
10. cancelling a pending boundary leaves committed time and state unchanged;
11. loading old history and diverging creates a new timeline whose events and
    memories do not leak from the abandoned future;
12. the same package, save, and command fixtures produce the same committed
    digest through the Godot host and engine-neutral evaluator;
13. querying available interactions is read-only, and typed execution compiles
    into the same event transaction with fresh cost and cooldown checks;
14. interaction retries cannot consume a cost or create a cooldown twice;
15. Int64 and scaled fixed-point fixtures produce identical results across
    engines, while overflow, inexact rejected division, incompatible units,
    and binary-float authority attempts fail closed;
16. two game-defined channels representing co-located and remote interaction
    return only definitions admitted by game-owned reachability and capability
    predicates;
17. player confirmation against a stale catalog or state version requires a
    fresh query and cannot execute the prior definition;
18. a multi-path update and both sides of an entity-to-entity transfer commit
    together or roll back together, while directional relationships remain
    directional;
19. request, authoritative receipt, and continued event planning use one typed
    path, and an unknown receipt reconciles without redispatch;
20. a periodic actor batch persists partial participant completion and rejects
    a late result whose batch or draft fence is stale;
21. committed dialogue and event presentation survive reload with stable
    audience and content revision;
22. private actor memory and revision-bound group memory remain isolated across
    concurrent decisions and membership changes;
23. hot catalog publication affects only new transactions, while a pending
    event, choice, or actor batch retains its captured revisions;
24. a periodic event and an executed interaction produce the same effect
    request/receipt evidence shape.

Unity and Unreal add the same fixture suite as their world-layer integrations
arrive. They do not get engine-specific package semantics.

## Deliberate V1 omissions

V1 does not include:

- continuous physics or frame-driven event evaluation;
- arbitrary executable scripts in data packages;
- unrestricted model-authored events, effects, or tools;
- automatic lore-to-rule generation;
- multiplayer replication or authority;
- irreversible external effects inside an atomic world draft;
- compatibility export to source character or world-book formats;
- a complete consumer-facing no-code application.

These omissions keep the first world loop small enough to verify: import
authored content, issue a typed command, deterministically evolve state, call
an agent only where fixed logic is insufficient, and export a portable world.

## Related documents

- [Architecture](architecture.md)
- [Game semantics](game-semantics.md)
- [Getting started](getting-started.md)
- [Tools, skills, and memory](tools-skills-memory.md)
- [Final-output admission](final-output-admission.md)
