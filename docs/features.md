# Feature and API map

This page maps product needs to the smallest reusable OpenGameAgent primitive.

## Agent behavior

| Need | API |
| --- | --- |
| Stream interleaved commentary, reasoning, tool calls, and final answers | `Agent.Subscribe`, `AgentEvent`, `ModelStreamEvent`, `AgentTextPhase` |
| Multi-step plan and action loop | `Agent`, `AgentTool` |
| Stream typed partial tool output or generated previews | `ToolExecutionContext.ReportProgressAsync`, `ToolProgress.Content` |
| Interrupt or amend current work | `Agent.Steer`, `Agent.Abort` |
| Queue the next interaction | `Agent.FollowUp` |
| Change prompts or context per turn | `AgentHooks` |
| Validate an imported canonical transcript | `AgentValidation.ValidateTranscript` |
| Restrict latency and resource use | `AgentLimits`, `ModelParameters` |
| Keep independent tools parallel around ordered barriers | `ToolExecutionMode`, `AgentTool.ExecutionMode`, `AgentLimits.MaxConcurrentTools` |
| Detect and stop exact prepared tool-call loops | `AgentLimits.ExactToolRepeatAdvisoryThreshold`, `AgentLimits.ExactToolRepeatTerminationThreshold`, `AgentEvent.ToolRepeat` |
| Enforce a model-call deadline | `AgentLimits.ModelTimeoutMilliseconds` |
| Retry transient model failures | `RetryingModelProvider` |
| Fall back across endpoints/models | `FallbackModelProvider` |
| Compact before exceeding context | `IGameTranscriptCompactor`, `SummarizingGameTranscriptCompactor`, `ModelGameTranscriptSummarizer`, model context-window settings |
| Run low-latency speech without blocking the agent loop | `RealtimeConversationManager` |
| Hand complex speech turns to the authoritative game agent | `GameRealtimeAgentBridge` |
| Cancel played audio when the player interrupts | `IRealtimeTransportSession.TruncateAudioAsync` |
| Run replaceable gaze/gesture/expression behaviors | `IRealtimeBehaviorHandler` |

## Composition and extensions

| Need | API |
| --- | --- |
| Build an immutable runtime composition | `GameAgentBuilder` |
| Add context, tools, skills, hooks, prompts, or providers | `IGameAgentExtension`, `GameAgentExtensionApi` |
| Observe lifecycle without coupling extensions | `GameAgentExtensionEvents` |
| Exchange typed extension messages | `GameAgentExtensionChannel<T>` |
| Keep per-session extension state | `GameAgentExtensionState` |
| Inspect registrations and conflicts | `GameAgentExtensionHost.GetResources`, `GetDiagnostics` |
| Hide collected tools per input before model dispatch | `GameAgentExtensionApi.RegisterToolVisibilityPolicy`, `GameToolVisibilityContext` |
| Gate, deny, or rewrite a tool call | `ToolPolicyExtension`, `IGameToolPolicy` |
| Require durable host approval before a high-risk call | `ToolApprovalExtension`, `IGameToolApprovalBroker` |
| Search a large tool catalog on demand | `ToolCatalogExtension`, `IGameToolCatalog` |
| Ask the player structured questions and recommend choices | `StructuredInteractionExtension`, `IGameInteractionBroker` |
| Track goals and resume them after game-time waits | `GoalLoopExtension` |
| Keep a persistent ordered checklist with host-verified progress and durable pause/resume | `TaskPlanExtension` |
| Turn an NPC's structured post-task reflection and host-verifiable evidence into immutable versioned skills, including ordered composite procedures over currently available tools, with disabled/review/validated-auto activation, evaluation, demotion, and exact-version rollback | `BehaviorLearningExtension` |
| Publish a validated immutable behavior for discovery by a game/world/role/faction, then require explicit per-NPC adoption and keep each actor's evaluation isolated | `SharedBehaviorCatalogExtension`, `IGameSharedBehaviorStore` |
| Delegate bounded foreground or background work | `AgentDelegationExtension` |
| Query a game-owned knowledge source | `ExternalKnowledgeExtension` |
| Capture bounded lifecycle traces | `GameAgentTracingExtension` |
| Persist, inspect, and evaluate traces without replaying side effects | `JsonLinesGameAgentTraceSink`, `GameAgentTraceHtmlReport`, `GameAgentTraceEvaluator` |
| Attribute queue, provider, framework, tool, and authoritative-host latency | `GameAgentPerformanceSummary`, `GameActionDispatchTimings` |
| Benchmark fixed/fake providers, tools, faults, and concurrency | `GameAgentBenchmarkRunner` |
| Observe STT/TTS/barge-in and media asset-ready latency | `RealtimeMetricsCollector`, `GameMediaMetricsCollector` |
| Load a portable package of skills and MCP servers | `AgentPluginLoader`, `AgentPluginPackage` |

## Game integration

| Need | API |
| --- | --- |
| Submit non-language game data | `GameInput.PayloadJson` |
| Attach screenshots or other visual observations | `GameInput.Content`, `BinaryContent`, `GameImageAttachment` |
| Persist and resolve immutable image input | `IGameImageAttachmentStore`, `FileGameImageAttachmentStore` |
| Express game time or save forks | `GameMoment` |
| Supply current world state | `IGameContextProvider`, `GameContextSlice` |
| Keep obvious dialogue fast | expose only relevant context and tools; a direct assistant message ends after one provider request |
| Continue from a tool call | built into the unified message-or-tool Agent loop |
| Deny optional persistent planning per actor | `GameExecutionScopeProvider`, `GameExecutionScope.NoOptionalCapabilities` |
| Keep one NPC ordered | built into `GameAgentRuntime` |
| Run many NPCs concurrently | `GameRuntimeLimits.MaxConcurrentActors`, `MultiActorScheduler` |
| Correct or cancel an active NPC run | `GameAgentRuntime.TrySteer`, `GameAgentRuntime.TryAbort` |
| Persist transcripts and deduplicate inputs | `IGameSessionStore` |
| Read the current persisted transcript without parsing a store | `GameAgentRuntime.ReadTranscriptAsync`, `ServerGameAgentClient.ReadTranscriptAsync` |
| Consume one versioned cross-language run/item stream with reconnect and exact control | `OpenGameAgent.Runtime.Protocol`, `GameRuntimeServerClient` |
| Read capabilities, complete usage, and durable action exchange from C# | `ServerGameAgentClient.ReadCapabilitiesAsync`, `ReadUsageAsync`, `ClaimActionsAsync`, `StreamActionsAsync`, `SubmitActionReceiptAsync`, `ReconcileActionAsync` |
| Compact a long transcript with a bounded zero-tool model request | `SummarizingGameTranscriptCompactor`, `ModelGameTranscriptSummarizer` |

## World actions and simulation

| Need | API |
| --- | --- |
| Mutate game state through a typed tool | `GameActionTool` |
| Avoid repeating uncertain writes | `DurableGameActionDispatcher`, `IGameActionJournal` |
| Execute on an engine-owned main thread | wrap the authoritative `IGameActionHandler` in `QueuedGameActionHandler`, then call `Pump` from the engine thread |
| Store long-term NPC facts/events | `IGameMemoryStore`, `GameMemory` |
| Apply custom semantic ranking | `IGameMemoryRanker`, `RankedGameMemoryStore` |
| Add local or remote vector embeddings and single-snapshot hybrid recall | `IMemoryEmbeddingProvider`, `IGameMemorySearchSnapshotSource`, `VectorMemoryStore` |
| Run BGE-M3 INT8 embeddings in-process without Python or a model service | `BgeM3OnnxEmbeddingProvider` (`OpenGameAgent.Memory.Onnx`) |
| Inspect or explicitly rebuild vectors after a model change | `RuntimeMemoryLifecycle`, `VectorMemoryStatus` |
| Migrate flat memory/vector files into bounded session/owner partitions | `FileGameMemoryStore.MigrateLegacyLayoutAsync`, `FileVectorMemoryIndex.MigrateLegacyIndexAsync` |
| Attribute context and recall latency without recording content | `context.provider.completed`, `memory.search.completed`, `GameAgentPerformanceSummary` |
| Add reusable behavior instructions | `IGameSkillSource`, `GameSkill` |
| Load portable or game-filtered skills | `DirectoryGameSkillSource` (`SKILL.md` or `skill.json`) |
| Load reusable prompt templates with bounded arguments | `FileGamePromptTemplateLoader`, `GamePromptTemplate` |
| Trigger and save monthly/daily/turn events | `GameTimeScheduler`, `CaptureState` |
| Send work between persistent actors | `IGameMailbox` |
| Keep a semantic action alive across game-time ticks | `TaskPlanExtension`, `GoalLoopExtension`, `GameTimeScheduler`, `IGameMailbox` |
| Narrate long-running action progress without replaying its mutation | durable action receipt plus later structured `GameInput` observations |
| Run fixed multi-stage game logic | game-owned state machine plus registered Agent inputs/tools where semantic judgment is needed |
| Run durable dependency graphs | game-owned scheduler; use OGA durable actions for every world write |
| Compose review, draft, validation, repair, and publication stages | game-owned orchestration, validators, and commit tools |
| Generate a planner graph or behavior asset | structured model output, game-owned compiler/validator, durable publication tool |
| Generate images/audio/video | `GameMediaGenerator`, `GameMediaRegistry` |
| Call official OpenAI image generation/edit endpoints | `OpenAIImageGenerator` |
| Call Volcengine Ark/Seedream with reference images and explicit sizes | `VolcengineImageGenerator` |
| Use trusted local image/audio/video services | `ComfyUiImageGenerator`, `LocalAiMediaGenerator` |
| Materialize generated media as durable game assets | `DurableGameMediaPipeline`, `GameMediaResourceStore` |
| Expose persistent asset generation as an Agent Tool | `createDurableGameMediaTool` |
| Recover an interrupted asset import without repeating the mutation | `DurableGameMediaPipeline.resumeImport` |
| Spill large tool output and retrieve it later | `ArtifactExtension`, `IGameAgentArtifactStore` |
| Recall scoped memory through an extension | `GameMemoryExtension` |

## Models, credentials, and external tools

| Need | API |
| --- | --- |
| Describe capabilities, context, output limits, reasoning, and cost | `GameModelDescriptor` |
| Register and select local or remote models | `GameModelCatalog` |
| Validate a provider's normalized stream, cancellation, bounds, and diagnostic secrecy | `GameProviderConformance`, `GameProviderConformanceFixtures` |
| Refresh a provider's model list safely | `GameModelProviderRegistration.RefreshModels`, `GameModelCatalog.RefreshAsync` |
| Resolve API keys, OAuth-style tokens, or local/no-auth modes | `IGameProviderAuthentication`, `IGameCredentialStore` |
| Persist player-supplied credentials for the current Windows user | `WindowsDpapiGameCredentialStore` in `OpenGameAgent.Models.Credentials.Windows` |
| Load the bundled model directory as executable providers | `BuiltInGameModelRuntime` |
| Read durable per-session usage and explicit known/unknown cost | `GameAgentRuntime.ReadUsageAsync`, `GameSessionUsageLedger` |
| Read usage caused by one input | `GameAgentRunResult.RunUsage` |
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
- ordinary-tool run-operation journals with explicit replay/recovery policy;
- memories;
- mailboxes;
- agent artifacts;
- delegation records;
- directory-backed skills;
- directory-backed prompt templates.
- content-addressed local image attachments (`OpenGameAgent.Attachments.Local`).
- generated-asset jobs and content-addressed generated resources (`OpenGameAgent.Persistence`).

File stores coordinate writers that use the same directory through cross-process leases, but they are not a distributed database. A multiplayer or multi-host service should implement the same interfaces using transactional shared storage and explicit actor ownership. Completed action, mailbox, and deduplication records are intentionally retained to preserve replay safety; long-running products should implement retention or archival in their game-owned stores rather than deleting evidence blindly.

## Deliberately game-owned

OpenGameAgent does not prescribe:

- entity or component schemas;
- pathfinding, animation, physics, combat, inventory, quests, or construction code;
- who can observe which data;
- NPC activation and level-of-detail policy;
- embedding weights and installation (the optional ONNX package provides an in-process runtime but never downloads or bundles a model);
- model vendor, prompt catalog, or monetization;
- visual UI, world editor, or downloadable world-package format.

Expose these capabilities as context, tools, stores, or scheduling policy. For example, a construction agent does not require a special construction subsystem in the runtime: the game exposes bounded tools such as `inspect_area`, `estimate_materials`, and `place_blueprint`, then executes the resulting plan with its normal building code.
