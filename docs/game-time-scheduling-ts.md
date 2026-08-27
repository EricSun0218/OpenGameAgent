# Game-time signals and schedules

`@opengameagent/scheduling` lets a host turn authoritative game-time changes into durable Agent inputs. It deliberately does not use wall-clock timers: the host advances game time, and pausing the game pauses the scheduler.

## Contract

- A schedule belongs to an exact `GameSessionKey`, including timeline and generation.
- One-shot and fixed game-tick recurring schedules are supported.
- `advance` atomically materializes due occurrences with stable IDs and advances every affected schedule.
- Repeating the same advance ID returns the same occurrences and never creates another occurrence.
- Occurrences use leases. A crash before `complete` makes the same occurrence claimable after lease expiry; it does not invent a new signal ID.
- Different actors, saves, timelines, and generations are isolated.
- Scheduling, advancing, claiming, and completion do not call a model. The host decides which signals are relevant enough to run an Agent.

```ts
using schedules = new SqliteGameTimeScheduler("world-ai.db");

await schedules.schedule({
  id: "monthly-life",
  session,
  kind: "world.monthly-life",
  payload: { source: "calendar" },
  due: { tick: 30 },
  intervalTicks: 30,
});

await schedules.advance(
  {
    id: "calendar-step-12",
    session,
    fromExclusive: { tick: 29 },
    toInclusive: { tick: 60, calendar: "year-1-month-2" },
  },
  128,
);

for (const delivery of await schedules.claim(session, 16, Date.now(), 30_000)) {
  const input = gameSignalToInput(delivery.occurrence);
  // Apply host relevance/budget policy, then pass input to the runtime when needed.
  await schedules.complete(session, delivery.occurrence.id, delivery.leaseToken);
}
```

The scheduler is an outbox, not an exactly-once game-side effect engine. Consumers must use the stable occurrence ID for idempotency. Authoritative world writes still go through durable game actions and receipts.

Completed occurrences, terminal schedules, and advance idempotency records have configurable bounded retention. Reusing an advance ID after it has aged out of retention is outside the idempotency window.
