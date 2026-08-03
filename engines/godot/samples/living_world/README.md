# Living-world patterns

This deterministic scene covers four game-facing patterns without a model key:
direct dialogue, a game-calendar trigger, bounded multi-NPC scheduling, and a
four-stage authoritative construction workflow. Run
`LivingWorldPatternsSample.tscn`; it prints `GODOT_LIVING_WORLD_SAMPLE_PASS`.

The policy decides when an actor deserves Agent work. The workflow orders
game-owned commands. Actual legality and state mutation remain in registered
Godot main-thread handlers.
