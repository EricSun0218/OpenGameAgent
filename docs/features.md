# Feature and API map

This page maps product needs to the smallest reusable OpenGameAgent primitive.

## Agent behavior

| Need | API |
| --- | --- |
| Stream dialogue or reasoning | `Agent.Subscribe`, `AgentEvent`, `ModelStreamEvent` |
| Multi-step plan and action loop | `Agent`, `AgentTool` |
| Stream typed partial tool output or generated previews | `ToolExecutionContext.ReportProgressAsync`, `ToolProgress.Content` |
| Interrupt or amend current work | `Agent.Steer`, `Agent.Abort` |
| Queue the next interaction | `Agent.FollowUp` |
| Change prompts or context per turn | `AgentHooks` |
| Validate an imported canonical transcript | `AgentValidation.ValidateTranscript` |
| Restrict latency and resource use | `AgentLimits`, `ModelParameters` |
| Enforce a model-call deadline | `AgentLimits.ModelTimeoutMilliseconds` |
| Retry transient model failures | `RetryingModelProvider` |
| Fall back across endpoints/models | `FallbackModelProvider` |
| Compact before exceeding context | `IGameTranscriptCompactor`, model context-window settings |
| Run low-latency speech without blocking the agent loop | `RealtimeConversationManager` |
| Hand complex speech turns to the authoritative game agent | `GameRealtimeAgentBridge` |
| Cancel played audio when the player interrupts | `IRealtimeTransportSession.TruncateAudioAsync` |
| Run replaceable gaze/gesture/expression behaviors | `IRealtimeBehaviorHandler` |

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
| Keep a persistent ordered checklist with host-verified progress and durable pause/resume | `TaskPlanExtension` |
| Delegate bounded foreground or background work | `AgentDelegationExtension` |
| Query a game-owned knowledge source | `ExternalKnowledgeExtension` |
| Capture bounded lifecycle traces | `GameAgentTracingExtension` |
| Persist, inspect, and evaluate traces without replaying side effects | `JsonLinesGameAgentTraceSink`, `GameAgentTraceHtmlReport`, `GameAgentTraceEvaluator` |
| Load a portable package of skills and MCP servers | `AgentPluginLoader`, `AgentPluginPackage` |

## Game integration

| Need | API |
| --- | --- |
| Submit non-language game data | `GameInput.PayloadJson` |
| Attach screenshots or other visual observations | `GameInput.Content`, `BinaryContent`, `GameImageAttachment` |
| Persist and resolve immutable image input | `IGameImageAttachmentStore`, `FileGameImageAttachmentStore` |
| Express game time or save forks | `GameMoment` |
| Supply current world state | `IGameContextProvider`, `GameContextSlice` |
| Keep obvious dialogue fast | `AutomaticGameRoutePolicy`, `ModelGameRouteClassifier` |
| Force a known path | `agent.route` input metadata |
| Keep one NPC ordered | built into `GameAgentRuntime` |
| Run many NPCs concurrently | `GameRuntimeLimits.MaxConcurrentActors`, `MultiActorScheduler` |
| Correct or cancel an active NPC run | `GameAgentRuntime.TrySteer`, `GameAgentRuntime.TryAbort` |
| Persist transcripts and deduplicate inputs | `IGameSessionStore` |
| Build a standalone append-only branch/lane audit history | `IGameSessionHistoryRepository`, `GameSessionHistory` |
| Fork, search, page, or project that explicit history | `GameSessionHistory`, `GameHistoryContextProjection` |
| Compact a long transcript | `IGameTranscriptCompactor` |

## World actions and simulation

| Need | API |
| --- | --- |
| Mutate game state through a typed tool | `GameActionTool` |
| Avoid repeating uncertain writes | `DurableGameActionDispatcher`, `IGameActionJournal` |
| Execute on an engine-owned main thread | wrap the authoritative `IGameActionHandler` in `QueuedGameActionHandler`, then call `Pump` from the engine thread |
| Store long-term NPC facts/events | `IGameMemoryStore`, `GameMemory` |
| Apply custom semantic ranking | `IGameMemoryRanker`, `RankedGameMemoryStore` |
| Add local or remote vector embeddings and hybrid recall | `IMemoryEmbeddingProvider`, `VectorMemoryStore` |
| Inspect or explicitly rebuild vectors after a model change | `RuntimeMemoryLifecycle`, `VectorMemoryStatus` |
| Add reusable behavior instructions | `IGameSkillSource`, `GameSkill` |
| Load portable or game-filtered skills | `DirectoryGameSkillSource` (`SKILL.md` or `skill.json`) |
| Load reusable prompt templates with bounded arguments | `FileGamePromptTemplateLoader`, `GamePromptTemplate` |
| Trigger and save monthly/daily/turn events | `GameTimeScheduler`, `CaptureState` |
| Send work between persistent actors | `IGameMailbox` |
| Resume fixed multi-stage logic | `DurableGameWorkflow` |
| Run durable dependency graphs with bounded parallel branches | `DurableGameWorkflowGraph` |
| Generate images/audio/video | `IGameMediaGenerator`, `GameMediaGenerationTool` |
| Route generation by provider/model and media capability | `GameMediaModelRegistry` |
| Spill large tool output and retrieve it later | `ArtifactExtension`, `IGameAgentArtifactStore` |
| Recall scoped memory through an extension | `GameMemoryExtension` |

## Models, credentials, and external tools

| Need | API |
| --- | --- |
| Describe capabilities, context, output limits, reasoning, and cost | `GameModelDescriptor` |
| Register and select local or remote models | `GameModelCatalog` |
| Refresh a provider's model list safely | `GameModelProviderRegistration.RefreshModels`, `GameModelCatalog.RefreshAsync` |
| Resolve API keys, OAuth-style tokens, or local/no-auth modes | `IGameProviderAuthentication`, `IGameCredentialStore` |
| Load the bundled model directory as executable providers | `BuiltInGameModelRuntime` |
| Read durable per-session usage and explicit known/unknown cost | `GameAgentRuntime.ReadUsageAsync`, `GameSessionUsageLedger` |
| Register supported browser/device authorization flows | `BuiltInGameOAuthRegistration` |
| Observe bounded provider response metadata | `ProviderResponseObserver` |
| Fetch short-lived developer-hosted credentials | `DeveloperGatewayProvider`, `HttpDeveloperGatewayCredentialSource` |
| Run the same provider behind a trusted remote service | `RemoteModelProvider`, `ModelProviderProxyServer` |
| Connect to a compatible message-gateway service | `MessageGatewayProvider` |
| Use external tool servers without loading every schema into context | `McpToolConnectorExtension` (default `OnDemand`) |
| Expose every remote tool natively when the catalog is small | `GameMcpToolExposure.Direct` |
| Load Agent Plugins 1.0.0 with fixed-location discovery and failure isolation | `OpenGameAgent.Plugins` |

## Included stores

In-memory implementations are useful for tests and short-lived sessions. The `OpenGameAgent.Persistence` package includes local-file stores for:

- game sessions;
- append-only session histories with branches, lanes, records, and usage statistics;
- action journals;
- workflow checkpoints;
- memories;
- mailboxes;
- agent artifacts;
- delegation records;
- directory-backed skills;
- directory-backed prompt templates.
- content-addressed local image attachments (`OpenGameAgent.Attachments.Local`).

File stores coordinate writers that use the same directory through cross-process leases, but they are not a distributed database. A multiplayer or multi-host service should implement the same interfaces using transactional shared storage and explicit actor ownership. Completed action, workflow, mailbox, and deduplication records are intentionally retained to preserve replay safety; long-running products should implement retention or archival in their game-owned stores rather than deleting evidence blindly.

## Deliberately game-owned

OpenGameAgent does not prescribe:

- entity or component schemas;
- pathfinding, animation, physics, combat, inventory, quests, or construction code;
- who can observe which data;
- NPC activation and level-of-detail policy;
- embedding model runtime or service (the optional memory package provides the contract and derived index);
- model vendor, prompt catalog, or monetization;
- visual UI, world editor, or downloadable world-package format.

Expose these capabilities as context, tools, workflows, stores, or scheduling policy. For example, a construction agent does not require a special construction subsystem in the runtime: the game exposes bounded tools such as `inspect_area`, `estimate_materials`, and `place_blueprint`, then executes the resulting plan with its normal building code.
