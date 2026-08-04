# Living-world patterns

Add `LivingWorldPatternsSample` to an empty GameObject. It validates four
model-free patterns: direct dialogue admission, game-calendar triggers,
bounded multi-NPC scheduling, and a four-stage authoritative construction
workflow. The Console prints `UNITY_LIVING_WORLD_SAMPLE_PASS`.

Replace the sample workflow step kinds with narrow handlers owned by the game.
The runtime schedules and orchestrates; the game still validates every state
mutation.
