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
| Enforce a model-call deadline | `AgentLimits.ModelTimeoutMilliseconds` |
| Retry transient model failures | `RetryingModelProvider` |
| Fall back across endpoints/models | `FallbackModelProvider` |
| Compact before exceeding context | `IGameTranscriptCompactor`, model context-window settings |

## Composition and extensions

| Need | API |
| --- | --- |
| Build an immutable runtime composition | `GameAgentBuilder` |
| Add context, tools, skills, routes, workflows, hooks, prompts, or providers | `IGameAgentExtension`, `GameAgentExtensionApi` |
| Observe lifecycle without coupling extensions | `GameAgentExtensionEvents` |
| Exchange typed extension messages | `GameAgentExtensionChannel<T>` |
| Keep per-session extension state | `GameAgentExtensionState` |
| Inspect registrations and conflicts | `GameAgentExtensionHost.GetResources`, `GetDiagnostics` |
| Gate, deny, or rewrite a tool call | `ToolPolicyExtension`, `IGameToolPolicy` |
| Search a large tool catalog on demand | `ToolCatalogExtension`, `IGameToolCatalog` |
| Ask the player structured questions and recommend choices | `StructuredInteractionExtension`, `IGameInteractionBroker` |
| Track goals and resume them after game-time waits | `GoalLoopExtension` |
| Delegate bounded foreground or background work | `AgentDelegationExtension` |
| Query a game-owned knowledge source | `ExternalKnowledgeExtension` |
| Capture bounded lifecycle traces | `GameAgentTracingExtension` |

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
| Run durable dependency graphs with bounded parallel branches | `DurableGameWorkflowGraph` |
| Generate images/audio/video | `IGameMediaGenerator`, `GameMediaGenerationTool` |
| Spill large tool output and retrieve it later | `ArtifactExtension`, `IGameAgentArtifactStore` |
| Recall scoped memory through an extension | `GameMemoryExtension` |

## Models, credentials, and external tools

| Need | API |
| --- | --- |
| Describe capabilities, context, output limits, reasoning, and cost | `GameModelDescriptor` |
| Register and select local or remote models | `GameModelCatalog` |
| Refresh a provider's model list safely | `GameModelProviderRegistration.RefreshModels`, `GameModelCatalog.RefreshAsync` |
| Resolve API keys, OAuth-style tokens, or local/no-auth modes | `IGameProviderAuthentication`, `IGameCredentialStore` |
| Fetch short-lived developer-hosted credentials | `DeveloperGatewayProvider`, `HttpDeveloperGatewayCredentialSource` |
| Use external tool servers without loading every schema into context | `McpToolConnectorExtension` (default `OnDemand`) |
| Expose every remote tool natively when the catalog is small | `GameMcpToolExposure.Direct` |

## Included stores

In-memory implementations are useful for tests and short-lived sessions. The `OpenGameAgent.Persistence` package includes local-file stores for:

- game sessions;
- action journals;
- workflow checkpoints;
- memories;
- mailboxes;
- agent artifacts;
- delegation records;
- directory-backed skills.

File stores coordinate writers that use the same directory through cross-process leases, but they are not a distributed database. A multiplayer or multi-host service should implement the same interfaces using transactional shared storage and explicit actor ownership. Completed action, workflow, mailbox, and deduplication records are intentionally retained to preserve replay safety; long-running products should implement retention or archival in their game-owned stores rather than deleting evidence blindly.

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
