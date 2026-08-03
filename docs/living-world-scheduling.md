# Living-world scheduling

`GameAgent.Simulation` converts game-owned relevance signals into a bounded,
deterministic Agent-work plan. It is a level-of-detail policy, not a world
simulation and not an NPC rules engine.

The game submits its current game tick and one `LivingWorldActorSignal` per
eligible actor. Signals may include direct player input, visibility, combat,
pending messages and triggers, distance, salience, last evaluation tick, and
estimated token/step cost. Distances and salience support finite floating-point
values; clocks and revisions use exact integers.

The policy returns one decision per actor:

- `run_now`: admit an Agent decision;
- `coalesce`: keep pending work but combine it before a later run;
- `aggregate`: fold dormant events through deterministic game simulation;
- `skip`: no cognition is due.

Foreground actors use the interactive provider workload. Nearby and
background actors use background admission. Aging promotes starved background
work, but foreground work remains first. Actor ordering and tie breaks are
stable, so input collection order cannot change the result.

```csharp
var plan = new LivingWorldPolicy(options).Plan(
    new LivingWorldCycle { WorldId = worldId, GameTick = monthTick },
    actorSignals);

foreach (var decision in plan.Runnable)
{
    // Capture one immutable, perspective-correct snapshot here.
    // Then start Direct, Agent, or Workflow work with decision.WorkloadClass.
}
```

The policy does not decide who may trade, fight, remember, build, or speak.
Those rules stay in game code. A dormant actor can advance through deterministic
economy or schedule rules without a model call, then receive one summarized
observation when promoted.

## Game-specific biases handled explicitly

- simulation time is independent of wall-clock time;
- save forks, timelines, state revisions, and entity incarnations can invalidate
  old context;
- each observer sees a different projection of the same world;
- off-screen actors need a cheaper cognition level;
- player-facing latency and background throughput have different priorities;
- several NPCs can deliberate concurrently against one immutable snapshot;
- writes require game authority and may have an unknown outcome;
- low-level movement and frame decisions remain deterministic game AI;
- event bursts should coalesce instead of producing one model call per event;
- memory validity and delayed delivery may follow named game clocks.

The Godot and Unity packages ship a `LivingWorldPatterns` sample covering
dialogue, month-style triggers, multi-NPC admission, dormant aggregation, and
a fixed multi-step construction workflow.
