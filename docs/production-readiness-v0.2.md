# Production readiness v0.2

This milestone turns the runtime from an engine-embedded library into a
deployment-ready developer platform without moving game authority into the
framework.

## Package boundaries

```text
GameAgent.Protocol                 closed wire contracts
        |
GameAgent.Core                     engine-neutral Agent mechanics
        |
GameAgent.Runtime                  supported in-process composition
        |
        +-- GameAgent.Hosting      .NET server lifecycle and remote action bridge
        +-- GameAgent.Remote.Client     engine-compatible remote action connector
        +-- GameAgent.Storage.Relational provider-neutral relational journal
        +-- GameAgent.Storage.Sqlite     embedded durable storage
        +-- GameAgent.Storage.Postgres   multi-instance durable storage
        +-- GameAgent.Simulation   living-world admission and activation policy
        +-- GameAgent.Evaluation   gameplay-quality scenario evaluation
        +-- GameAgent.Observability.OpenTelemetry
```

Hosting, relational storage, and OpenTelemetry target .NET 8 and stay outside
the engine packages. Simulation, evaluation, and the remote client target
`netstandard2.1`; simulation and the remote client ship with Godot and Unity.

## Deployment modes

| Mode | Runtime location | Action authority | Credentials |
| --- | --- | --- | --- |
| Embedded | Game client/process | Local game host | BYOK or short-lived token |
| Authoritative server | Game server process | Game server | Server secret or gateway |
| Sidecar | Adjacent .NET process | Remote game server through receipts | Server secret or gateway |

The versioned transport envelope can represent observations, controls, runtime
events, action requests, and receipts. The shipped sidecar bridge deliberately
implements only the authority-sensitive action-request/receipt path; a game
service owns its authenticated run/input API. Every bridged message has a
bounded payload and stable tenant/world/run routing. Losing a connection never
implies that a world mutation failed.

## Acceptance matrix

1. A fresh developer can run a deterministic Godot or Unity sample without a
   model credential.
2. A clean-room coding agent can integrate a blank project using only shipped
   packages and public documentation.
3. Server hosting exposes liveness/readiness, bounded tenant admission,
   shutdown draining, and a reconnect-safe event cursor.
4. SQLite passes restart, atomic batch, compare-and-swap, duplicate, and
   operation-reconciliation tests.
5. PostgreSQL passes the same contract against a real disposable database and
   proves competing writers cannot both commit one revision.
6. Living-world policy keeps player-facing work ahead of background
   simulation, coalesces dormant work, and produces deterministic decisions.
7. Evaluation scores character adherence, action legality, memory evidence,
   consistency, latency, token use, and cost without making model calls inside
   the evaluator.
8. OpenTelemetry export retains the runtime's closed low-cardinality metric
   dimensions and redacts run, actor, player, and world identities by default.
9. Soak and fault suites exercise long-running queues, provider failure,
   process restart, storage contention, and large dormant populations.

## Deliberate exclusions

- No account, billing, matchmaking, or game networking product.
- No fixed character, combat, inventory, world, or save schema.
- No arbitrary executable extension loader.
- No native ASR, vision model, reinforcement-learning trainer, or frame-level
  controller.
- No additional engine or operating-system release matrix.
