# Feature and API map

This page maps product needs to the smallest reusable OpenGameAgent primitive.

## Agent behavior

| Need | API |
| --- | --- |
| Stream dialogue or reasoning | `Agent.Subscribe`, `AgentEvent`, `ModelStreamEvent` |
| Multi-step plan and action loop | `Agent`, `AgentTool` |
| Interrupt or amend current work | `Agent.Steer`, `Agent.Abort` |
| Queue the next interaction | `Agent.FollowUp` |
| Change prompts or context per turn | `AgentHooks` |
| Validate an imported canonical transcript | `AgentValidation.ValidateTranscript` |
| Restrict latency and resource use | `AgentLimits`, `ModelParameters` |
| Retry transient model failures | `RetryingModelProvider` |
| Fall back across endpoints/models | `FallbackModelProvider` |

## Game integration

| Need | API |
| --- | --- |
| Submit non-language game data | `GameInput.PayloadJson` |
| Express game time or save forks | `GameMoment` |
| Supply current world state | `IGameContextProvider`, `GameContextSlice` |
| Keep obvious dialogue fast | `AutomaticGameRoutePolicy`, `ModelGameRouteClassifier` |
| Force a known path | `agent.route` input metadata |
| Keep one NPC ordered | built into `GameAgentRuntime` |
| Run many NPCs concurrently | `GameRuntimeLimits.MaxConcurrentActors`, `MultiActorScheduler` |
| Correct or cancel an active NPC run | `GameAgentRuntime.TrySteer`, `GameAgentRuntime.TryAbort` |
| Persist transcripts and deduplicate inputs | `IGameSessionStore` |
| Compact a long transcript | `IGameTranscriptCompactor` |

## World actions and simulation

| Need | API |
| --- | --- |
| Mutate game state through a typed tool | `GameActionTool` |
| Avoid repeating uncertain writes | `DurableGameActionDispatcher`, `IGameActionJournal` |
| Execute on engine main thread | implement `IGameActionHandler` by queueing into the engine, then await the receipt |
| Store long-term NPC facts/events | `IGameMemoryStore`, `GameMemory` |
| Apply custom semantic ranking | `IGameMemoryRanker`, `RankedGameMemoryStore` |
| Add reusable behavior instructions | `IGameSkillSource`, `GameSkill` |
| Load portable or game-filtered skills | `DirectoryGameSkillSource` (`SKILL.md` or `skill.json`) |
| Trigger and save monthly/daily/turn events | `GameTimeScheduler`, `CaptureState` |
| Send work between persistent actors | `IGameMailbox` |
| Resume fixed multi-stage logic | `DurableGameWorkflow` |
| Generate images/audio/video | `IGameMediaGenerator`, `GameMediaGenerationTool` |

## Included stores

In-memory implementations are useful for tests and short-lived sessions. The `OpenGameAgent.Persistence` package includes local-file stores for:

- game sessions;
- action journals;
- workflow checkpoints;
- memories;
- mailboxes;
- directory-backed skills.

The stores are single-process building blocks, not a distributed database. Use one store instance per directory. A multiplayer service can implement the same interfaces using its existing transactional storage. Completed action, workflow, mailbox, and deduplication records are intentionally retained to preserve replay safety; long-running products should implement retention or archival in their game-owned stores rather than deleting evidence blindly.

## Deliberately game-owned

OpenGameAgent does not prescribe:

- entity or component schemas;
- pathfinding, animation, physics, combat, inventory, quests, or construction code;
- who can observe which data;
- NPC activation and level-of-detail policy;
- vector database or embedding model;
- model vendor, prompt catalog, or monetization;
- visual UI, world editor, or downloadable world-package format.

Expose these capabilities as context, tools, workflows, stores, or scheduling policy. For example, a construction agent does not require a special construction subsystem in the runtime: the game exposes bounded tools such as `inspect_area`, `estimate_materials`, and `place_blueprint`, then executes the resulting plan with its normal building code.
