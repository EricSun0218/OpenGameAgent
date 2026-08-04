# Security model and threat assessment

This document describes the reusable Runtime's trust boundaries. A game must
extend this assessment for its own tools, economy, multiplayer authority,
content policy, player data, distribution platform, and model provider.

## Protected assets

- authoritative game state, saves, inventories, economies, and world history;
- provider credentials, short-lived access tokens, and tenant identity;
- player prompts, generated content, tool inputs/results, memories, and logs;
- operation IDs, receipts, journals, checkpoints, and recovery evidence;
- engine main-thread availability and bounded CPU, memory, disk, network,
  provider quota, and monetary budgets;
- release source, engine packages, checksums, and provenance.

## Trust boundaries

The model, model output, player-authored content, imported Skills, remote
content, and network responses are untrusted. Tool descriptors and Skill
snapshots become capabilities only after host registration and admission.

The game host is authoritative for business validation and state mutation. The
Runtime can request an action, but only an `ActionReceipt` from the game can
settle its outcome. Persistence implementations are trusted to provide the
documented atomicity and compare-and-swap behavior. A custom provider transport,
memory provider, middleware callback, tool handler, or remote action connector
crosses a trust boundary and must preserve the same limits and identity rules.

## Threats and controls

| Threat | Primary controls | Residual responsibility |
| --- | --- | --- |
| Prompt injection requests unauthorized tools or data | Immutable admitted tool/Skill snapshots, schema validation, visibility policy, narrow capability catalogs | The game must authorize every action and avoid exposing unrestricted shell, filesystem, network, reflection, or asset mutation |
| A retry duplicates a trade, build, attack, purchase, or other write | Durable dispatch intent, stable operation ID, effect classification, host receipts, uncertain-result reconciliation, no blind retry | Tool handlers must implement operation lookup/idempotency appropriate to the game |
| Stale model output mutates a new save, timeline, or entity incarnation | Run/attempt fencing, semantic coordinates, guarded resume, state and membership revisions | The game must supply and validate current coordinates before mutation |
| Concurrent NPCs race on shared resources | Deterministic conflict scopes, serialized conflicting writes, revision checks, bounded batches | Game-specific simultaneity and conflict policy remains game-owned |
| Malformed or infinite provider streams exhaust resources | Strict UTF-8/event parsing, request/response bounds, stream deadlines, budgets, cancellation, quarantined late callbacks | Integrators must configure realistic route, token, cost, and workload budgets |
| A provider credential leaks from a shipped client or redirect | Runtime credential indirection, HTTPS requirement, redirect rejection, sanitized failures | Commercial credentials belong behind a game service or in short-lived scoped tokens; BYOK credentials require protected local storage |
| Imported/generated media reads local files or downloads arbitrary hosts | Artifact size/digest validation, configured store, explicit host allowlist, loopback opt-in, staged host validation and commit | The game must scan and moderate content and use platform-appropriate storage permissions |
| Journal or memory reveals player data | Bounded storage interfaces and explicit application ownership | The Runtime does not encrypt local journals; the application owns consent, minimization, retention, deletion, encryption, and regulatory compliance |
| A callback blocks shutdown or the frame thread | Main-thread dispatch boundary, callback timeouts, bounded quarantine, asynchronous shutdown and drain evidence | Host dependencies captured by a detached callback must remain valid until it returns |
| A malicious contribution steals CI/release secrets | Read-only default workflow permissions, SHA-pinned actions, candidate code separated from secret-bearing privacy approval, protected artifacts | Repository rules, least privilege, private vulnerability reporting, token rotation, and maintainer review must be enabled on GitHub |
| A release artifact differs from reviewed source | Exact-SHA source scanning, deterministic package normalization, clean-room consumption, engine validation, checksums, SBOM and signed provenance | Consumers must verify release checksums/attestations and use an expected repository identity |

## Deployment variants

In-process placement reduces action latency but shares the game process's
availability and client trust level. Server placement protects commercial
credentials and centralizes quotas but adds a network and tenant-isolation
boundary. The authenticated remote action bridge supports either placement; it
does not transfer game authority to the Runtime.

For multiplayer games, the authoritative game server must validate and settle
state-changing actions even when an Agent loop runs in a client. Client-side
logic is always modifiable by the player.

## Known non-goals

The Runtime is not a secure sandbox for arbitrary native code, managed
assemblies, scripts, or unrestricted tools. It does not make a compromised
client authoritative, guarantee that model output is truthful, provide content
moderation policy, or encrypt application storage by default.

## Review triggers

Reassess this model when adding a new execution surface, transport, credential
flow, persistence backend, generated-content importer, plugin mechanism,
privileged workflow, engine target, or authoritative action path. A major change
must include abuse cases, negative tests, resource limits, cancellation and
recovery behavior, and documentation of remaining application obligations.
