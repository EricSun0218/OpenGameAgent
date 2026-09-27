import {
	type AfterToolCallContext,
	type AfterToolCallResult,
	Agent,
	type AgentMessage,
	type AgentTool,
	type AgentToolResult,
	type BeforeToolCallContext,
	type BeforeToolCallResult,
	type StreamFn,
	uuidv7,
} from "@earendil-works/pi-agent-core";
import {
	type AssistantMessage,
	type AssistantMessageEventStream,
	createAssistantMessageEventStream,
	type DeferredHandle,
	type Message,
} from "@earendil-works/pi-ai";
import {
	assertCanonicalEvent,
	assertCanonicalEventData,
	assertOpaqueIdentifier,
	type CanonicalEvent,
	type CanonicalEventData,
	canonicalizeJsonValue,
	captureRunAdmissionAuthority,
	type DurableTranscriptAssistantMessage,
	type DurableTranscriptDeferredHandle,
	type DurableTranscriptDeferredOpenedRecord,
	type DurableTranscriptMessage,
	type DurableTranscriptMessageCompletedRecord,
	type DurableTranscriptTurnClosedRecord,
	type DurableTranscriptUserMessage,
	eventAudience,
	type JsonValue,
	type MessageContent,
	normalizeJsonValue,
	normalizeJsonValueWithin,
	type RunCoordinate,
} from "@opengameagent/protocol";
import { Mutex } from "async-mutex";
import {
	captureContextLimits,
	GameContextSnapshotError,
	type LoadedGameContextSource,
	loadGameContextSources,
	type PreparedGameContextSnapshot,
	prepareLoadedGameContextSnapshot,
	validateContextConfiguration,
} from "./context-snapshot.js";
import type { GameDeferredResponseAdapter } from "./deferred-response.js";
import {
	DURABLE_TOOL_EXECUTION,
	type DurableExecutionAgentTool,
	DurableToolExecutionUnresolvedError,
} from "./durable-tool-execution.js";
import {
	captureTranscriptMessage,
	type DurableTranscript,
	TranscriptCheckpointCommitError,
	type TranscriptCheckpointResult,
	type TranscriptRevision,
	TranscriptUnsupportedDeferredError,
} from "./durable-transcript.js";
import { PiEventProjector } from "./event-projector.js";
import {
	type CapturedExactToolRepeatPolicy,
	captureExactToolRepeatPolicy,
	ExactToolRepeatTracker,
	exactToolRepeatAdvisoryMessage,
} from "./exact-tool-repeat.js";
import {
	contextContainsImages,
	defaultGameInputProjection,
	prepareInputProjectionConfiguration,
	validateGameInputProjection,
} from "./input-projection.js";
import {
	captureProviderProvenanceBinding,
	type ExactToolRepeatAdvisoryProvenanceInput,
	type ModelContextToolOwner,
	prepareModelRequestProvenanceTemplate,
} from "./model-context-provenance.js";
import { createProviderAttemptLifecycle } from "./provider-attempt.js";
import {
	captureRequestContribution,
	captureRequestSourceArray,
	InvalidRequestSourceError,
	validateRequestSources,
} from "./request-sources.js";
import { RunMetricsTracker, withRunMetrics } from "./run-metrics.js";
import { registerRuntimeGameAgentActivationRelease, runtimeGameAgentAuthority } from "./runtime-activation-registry.js";
import {
	boundedToolExecutionError,
	GameToolReportedError,
	GameToolResultError,
	prepareToolResultSnapshot,
	prepareTransformableToolResultSnapshot,
	textToolResultContentBytes,
} from "./tool-result-snapshot.js";
import {
	extendPreparedToolSnapshot,
	type PreparedToolSnapshot,
	prepareToolSnapshot,
	selectVisibleToolSnapshot,
	type VisibleToolSnapshot,
	validateToolSnapshotLimits,
} from "./tool-snapshot.js";
import type { TranscriptCompactor } from "./transcript-compaction.js";
import type {
	CanonicalEventListener,
	GameAgentOptions,
	GameContextLimits,
	GameContextSource,
	GameControlCommand,
	GameControlResult,
	GameDeferredAbandonResult,
	GameDeferredOperation,
	GameInput,
	GameInputProjectionLimits,
	GameInputProjector,
	GameModelStream,
	GameProviderAttempt,
	GameProviderAttemptStreamFn,
	GameRequestSource,
	GameRunHandle,
	GameRunResult,
	GameToolExecution,
	GameToolPolicy,
	GameToolResultTransformContext,
	GameToolResultTransformer,
	GameToolSnapshotLimits,
	NormalizedGameInput,
} from "./types.js";

interface ActiveRun {
	runId: string;
	turn: bigint;
	input: NormalizedGameInput;
	abortController: AbortController;
	abortRequested: boolean;
	controlOpen: boolean;
	stopPolicyFailed: boolean;
	reportedTokens: number;
	reportedTokenAdmission: "open" | "closed" | "failed";
	transcriptCompactionFailed: boolean;
	transcriptPersistenceFailed: boolean;
	providerProvenanceFailed: boolean;
	nextTranscriptContextMessages: readonly AgentMessage[] | undefined;
	projector: PiEventProjector;
	nextTranscriptUserContent?: readonly MessageContent[];
	nextTranscriptUserMessage?: DurableTranscriptUserMessage;
	transcriptTurnOpen: boolean;
	transcriptToolIntents: ActiveTranscriptToolIntent[];
	deferredResume?: DurableTranscriptDeferredOpenedRecord;
	deferredFetchPending: boolean;
	providerAttempt: GameProviderAttempt | undefined;
	exactToolRepeat: ExactToolRepeatTracker | undefined;
	exactToolRepeatAdvisory: Omit<ExactToolRepeatAdvisoryProvenanceInput, "content"> | undefined;
	repeatPolicyFailed: boolean;
	repeatResetMessages: Set<AgentMessage>;
	pendingFollowUpMessages: Set<AgentMessage>;
	metrics: RunMetricsTracker;
}

interface ActiveTranscriptToolIntent {
	readonly assistantRecordId: string;
	readonly sourceIndex: number;
	readonly call: Extract<DurableTranscriptAssistantMessage["content"][number], { type: "toolCall" }>;
	startRecordId?: string;
	durableResultContent?: readonly MessageContent[];
	finalizationFailed?: boolean;
	executionUnresolved?: boolean;
	completed: boolean;
}

interface PendingToolResultFinalization {
	readonly context: Omit<GameToolResultTransformContext, "isError">;
	readonly reportedError: boolean;
	readonly signal: AbortSignal;
	readonly transformers: readonly GameToolResultTransformer[];
	readonly requiresFinalAdmission: boolean;
}

const EMPTY_USAGE = {
	input: 0,
	output: 0,
	cacheRead: 0,
	cacheWrite: 0,
	totalTokens: 0,
	cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
} as const;

function isCompletedAssistantMessage(message: Readonly<AssistantMessage>): boolean {
	return message.stopReason === "stop" || message.stopReason === "length" || message.stopReason === "toolUse";
}

export class GameAgentBusyError extends Error {
	constructor() {
		super("The game Agent already has an active run.");
		this.name = "GameAgentBusyError";
	}
}

export class GameAgentIdentityError extends Error {
	constructor() {
		super("Game input does not match this Agent session and actor.");
		this.name = "GameAgentIdentityError";
	}
}

export class GameAgentDeferredPendingError extends Error {
	constructor(public readonly operationId: string) {
		super("The game Agent has a pending deferred provider operation.");
		this.name = "GameAgentDeferredPendingError";
	}
}

export function createProviderAttemptModelStream(stream: GameProviderAttemptStreamFn): GameModelStream {
	if (typeof stream !== "function") throw new TypeError("The provider-attempt model stream must be a function.");
	return Object.freeze({ kind: "attempts", stream });
}

export class GameAgent {
	private readonly agent: Agent;
	private readonly serial = new Mutex();
	private readonly listeners = new Map<CanonicalEventListener, Mutex>();
	private readonly pendingToolResults = new WeakMap<object, PendingToolResultFinalization>();
	private readonly contextSources: readonly GameContextSource[];
	private readonly contextLimits: GameContextLimits | undefined;
	private readonly requestSources: readonly GameRequestSource[];
	private readonly newId: () => string;
	private readonly now: () => number;
	private readonly metricsNow: () => number;
	private readonly sessionId: string;
	private readonly actorId: string;
	private readonly modelStream: GameModelStream;
	private readonly emitEvent: GameAgentOptions["emitEvent"];
	private readonly toolSource: GameAgentOptions["toolSource"];
	private readonly toolPolicy: GameToolPolicy | undefined;
	private readonly toolLimits: Readonly<GameToolSnapshotLimits> | undefined;
	private readonly exactToolRepeatPolicy: CapturedExactToolRepeatPolicy | undefined;
	private readonly beforeToolExecution: GameAgentOptions["beforeToolExecution"];
	private readonly projectInput: GameInputProjector | undefined;
	private readonly inputProjectionLimits: Readonly<GameInputProjectionLimits> | undefined;
	private readonly maxReportedTokensPerRun: number | undefined;
	private readonly transcript: DurableTranscript | undefined;
	private readonly transcriptCompactor: TranscriptCompactor | undefined;
	private readonly deferredResponses: GameDeferredResponseAdapter | undefined;
	private readonly providerProvenance: ReturnType<typeof captureProviderProvenanceBinding>;
	private durableTranscriptMessages: DurableTranscriptMessage[] = [];
	private transcriptRevision: TranscriptRevision = "empty";
	private transcriptCheckpointRevision: string | undefined;
	private transcriptStateUsable = true;
	private pendingDeferred: DurableTranscriptDeferredOpenedRecord | undefined;
	private pendingDeferredFinalOutput: string | undefined;
	private deferredTransition = false;
	private active: ActiveRun | undefined;

	constructor(options: GameAgentOptions) {
		assertOpaqueIdentifier(options.sessionId);
		assertOpaqueIdentifier(options.actorId);
		this.sessionId = options.sessionId;
		this.actorId = options.actorId;
		const transcriptBinding = options.transcript;
		let restoredMessages: AgentMessage[] = [];
		if (transcriptBinding) {
			const boundTranscript = transcriptBinding.transcript;
			const restore = transcriptBinding.restore;
			const restoreStream = restore.stream;
			const restoreSessionId = restoreStream.sessionId;
			const restoreActorId = restoreStream.actorId;
			if (restoreSessionId !== this.sessionId || restoreActorId !== this.actorId) {
				throw new TypeError("The durable transcript does not match this Agent session and actor.");
			}
			this.transcript = boundTranscript;
			this.transcriptRevision = restore.revision;
			this.transcriptCheckpointRevision = restore.checkpointRevision;
			restoredMessages = snapshotAgentMessages(restore.messages);
			this.durableTranscriptMessages = [...structuredClone(restore.durableMessages)];
			this.pendingDeferred = restore.pendingDeferred ? structuredClone(restore.pendingDeferred) : undefined;
			this.pendingDeferredFinalOutput = restore.pendingDeferredFinalOutput;
		} else {
			this.transcript = undefined;
		}
		if (options.transcriptCompactor && !this.transcript) {
			throw new TypeError("Transcript compaction requires a durable transcript.");
		}
		this.transcriptCompactor = options.transcriptCompactor;
		const deferredResponses = options.deferredResponses;
		this.deferredResponses = deferredResponses
			? Object.freeze({
					fetch: deferredResponses.fetch.bind(deferredResponses),
					cancel: deferredResponses.cancel.bind(deferredResponses),
				})
			: undefined;
		this.modelStream = captureModelStream(options.modelStream);
		this.emitEvent = options.emitEvent;
		this.toolSource = options.toolSource;
		if (options.toolPolicy !== undefined && typeof options.toolPolicy !== "function") {
			throw new TypeError("The Tool policy must be a function.");
		}
		if (
			options.toolSource !== undefined &&
			(typeof options.toolPolicy !== "function" || options.toolLimits === undefined)
		) {
			throw new TypeError("Tool sources require an explicit Tool policy and Tool snapshot limits.");
		}
		if (options.toolLimits !== undefined && options.toolPolicy === undefined) {
			throw new TypeError("Tool snapshot limits require an explicit Tool policy.");
		}
		this.toolPolicy = options.toolPolicy;
		this.toolLimits = options.toolLimits ? validateToolSnapshotLimits(options.toolLimits) : undefined;
		this.exactToolRepeatPolicy =
			options.exactToolRepeatPolicy === undefined
				? undefined
				: captureExactToolRepeatPolicy(options.exactToolRepeatPolicy, this.toolLimits?.maxTools);
		const requestSources = captureRequestSourceArray(options.requestSources, this.toolLimits?.maxRequestSources);
		const requestSourceCount = requestSources.length;
		const hasToolCandidates = options.toolSource !== undefined || requestSourceCount > 0;
		if (hasToolCandidates && (typeof options.toolPolicy !== "function" || this.toolLimits === undefined)) {
			throw new TypeError("Tool sources require an explicit Tool policy and Tool snapshot limits.");
		}
		this.beforeToolExecution = options.beforeToolExecution;
		const projection = prepareInputProjectionConfiguration(options.projectInput, options.inputProjectionLimits);
		this.projectInput = projection?.project;
		this.inputProjectionLimits = projection?.limits;
		this.maxReportedTokensPerRun = captureMaxReportedTokensPerRun(options.maxReportedTokensPerRun);
		this.newId = options.newId ?? uuidv7;
		this.now = options.now ?? Date.now;
		this.metricsNow = options.metricsNow ?? (() => performance.now());
		if (typeof this.metricsNow !== "function") throw new TypeError("metricsNow must be a function.");
		const contextLimits = captureContextLimits(options.contextLimits);
		this.contextSources = validateContextConfiguration(options.contextSources, contextLimits, requestSourceCount > 0);
		this.requestSources = validateRequestSources(
			requestSources,
			this.contextSources,
			this.toolLimits?.maxRequestSources,
		);
		this.contextLimits = contextLimits;
		const stopPolicy = options.shouldStopAfterTurn;
		const transformContext = options.transformContext;
		this.providerProvenance = captureProviderProvenanceBinding(options.providerProvenance, {
			hasTranscript: this.transcript !== undefined,
			systemPrompt: options.systemPrompt ?? "",
			hasContextTransformer: transformContext !== undefined,
			...(this.exactToolRepeatPolicy ? { exactToolRepeatPolicy: this.exactToolRepeatPolicy } : {}),
		});

		this.agent = new Agent({
			streamFn: (model, context, streamOptions) => this.streamWithFreshRequestState(model, context, streamOptions),
			beforeToolCall: (context, signal) => this.applyExactToolRepeatPolicy(context, signal),
			afterToolCall: (context, signal) => this.finalizeToolResult(context, signal),
			initialState: {
				model: options.model,
				systemPrompt: options.systemPrompt ?? "",
				tools: [],
				messages: restoredMessages,
			},
			...(this.transcript || transformContext || this.exactToolRepeatPolicy
				? {
						transformContext: async (messages: AgentMessage[], signal?: AbortSignal) => {
							const metrics = this.active?.metrics;
							metrics?.beginContext();
							try {
								const requestSignal = this.combineAbortSignals(signal, this.active?.abortController.signal);
								let transformed = messages;
								if (this.transcript) {
									transformed = [
										...(await this.transcript.materialize(
											{ sessionId: this.sessionId, actorId: this.actorId },
											this.durableTranscriptMessages,
											requestSignal,
										)),
									];
								}
								transformed = transformContext ? await transformContext(transformed, requestSignal) : transformed;
								const advisory = this.active?.exactToolRepeat?.modelAdvisory();
								return advisory
									? [...transformed, { role: "user", content: advisory, timestamp: this.now() }]
									: transformed;
							} finally {
								metrics?.endContext();
							}
						},
					}
				: {}),
			...(this.transcriptCompactor
				? {
						prepareNextTurnWithContext: (context) => {
							const active = this.active;
							const messages = active?.nextTranscriptContextMessages;
							if (!active || !messages) return undefined;
							active.nextTranscriptContextMessages = undefined;
							return { context: { ...context.context, messages: [...messages] } };
						},
					}
				: {}),
			...(stopPolicy || this.transcriptCompactor || this.exactToolRepeatPolicy || this.maxReportedTokensPerRun
				? {
						shouldStopAfterTurn: async (_context, signal) => {
							const active = this.active;
							if (!active) return true;
							if (active.repeatPolicyFailed || active.exactToolRepeat?.isTerminated()) return true;
							if (active.transcriptCompactionFailed) return true;
							if (active.reportedTokenAdmission !== "open") return true;
							if (!stopPolicy) return false;
							try {
								const stopSignal = this.combineAbortSignals(signal, active.abortController.signal);
								if (!stopSignal) throw new Error("The game stop-policy signal is unavailable.");
								return await stopPolicy(active.input, this.coordinate(active), stopSignal);
							} catch {
								active.stopPolicyFailed = true;
								return true;
							}
						},
					}
				: {}),
		});

		const detachPiEvents = this.agent.subscribe((event) => {
			const observedRun = this.active;
			if (
				observedRun &&
				(event.type === "message_update" || event.type === "message_end") &&
				isPiMessage(event.message) &&
				event.message.role === "assistant"
			) {
				if (hasProviderResponseContent(event.message)) observedRun.metrics.providerResponseObserved();
				if (event.type === "message_end") observedRun.metrics.providerSettled();
			}
			return this.serial.runExclusive(async () => {
				const active = this.active;
				if (active) {
					if (
						event.type === "tool_execution_end" &&
						active.transcriptToolIntents.some(
							(intent) =>
								intent.call.id === event.toolCallId && (intent.finalizationFailed || intent.executionUnresolved),
						)
					) {
						return;
					}
					if (event.type === "message_end" && isPiMessage(event.message)) {
						if (event.message.role === "user" && active.repeatResetMessages.delete(event.message)) {
							active.pendingFollowUpMessages.delete(event.message);
							active.exactToolRepeat?.reset();
						}
						if (event.message.role === "assistant") {
							active.exactToolRepeat?.beginAssistantMessage(
								event.message.content.filter((part) => part.type === "toolCall"),
							);
							this.captureReportedTokens(active, event.message);
						}
						await this.recordTranscriptMessage(active, event.message);
						if (
							event.message.role === "assistant" &&
							!active.transcriptPersistenceFailed &&
							!active.providerProvenanceFailed
						) {
							await this.recordProviderResponse(active, event.message);
						}
					}
					if (event.type === "turn_end") {
						active.exactToolRepeat?.finishAssistantMessage();
						await this.recordTranscriptTurnClosed(active, event.message);
					}
					if (event.type === "agent_end") {
						active.controlOpen = false;
						active.nextTranscriptContextMessages = undefined;
					}
					await active.projector.project(event);
				}
			});
		});
		registerRuntimeGameAgentActivationRelease(this, () => {
			detachPiEvents();
			this.listeners.clear();
		});
	}

	get isRunning(): boolean {
		return this.active !== undefined;
	}

	readActiveCoordinate(): RunCoordinate | undefined {
		return this.active ? this.coordinate(this.active) : undefined;
	}

	readPendingDeferred(): GameDeferredOperation | undefined {
		return this.pendingDeferred ? Object.freeze(structuredClone(this.pendingDeferred)) : undefined;
	}

	subscribe(listener: CanonicalEventListener): () => void {
		if (!this.listeners.has(listener)) this.listeners.set(listener, new Mutex());
		return () => this.listeners.delete(listener);
	}

	start(input: GameInput, requestedRunId?: string): GameRunHandle {
		if (!this.transcriptStateUsable) {
			throw new Error("The live Agent state no longer matches its durable transcript; restore a fresh Agent.");
		}
		if (this.deferredTransition) throw new GameAgentBusyError();
		if (this.pendingDeferred) throw new GameAgentDeferredPendingError(this.pendingDeferred.operationId);
		if (this.active) {
			throw new GameAgentBusyError();
		}
		const sessionId = input.sessionId;
		const actorId = input.actorId;
		const inputId = input.inputId;
		const authority = input.admissionAuthority;
		if (sessionId !== this.sessionId || actorId !== this.actorId) {
			throw new GameAgentIdentityError();
		}
		assertOpaqueIdentifier(inputId);
		const admissionAuthority = authority === undefined ? undefined : captureRunAdmissionAuthority(authority);

		const normalizedInput: NormalizedGameInput = {
			sessionId,
			actorId,
			inputId,
			payload: freezeJsonValue(normalizeJsonValue(input.payload)),
			...(admissionAuthority === undefined ? {} : { admissionAuthority }),
		};
		Object.freeze(normalizedInput);
		const runId = requestedRunId ?? this.newId();
		assertOpaqueIdentifier(runId);
		if (this.active || this.deferredTransition) throw new GameAgentBusyError();
		return this.activate(normalizedInput, runId);
	}

	resumeDeferred(operationId: string): GameRunHandle {
		if (!this.transcriptStateUsable) {
			throw new Error("The live Agent state no longer matches its durable transcript; restore a fresh Agent.");
		}
		if (this.active || this.deferredTransition) throw new GameAgentBusyError();
		assertOpaqueIdentifier(operationId);
		const pending = this.pendingDeferred;
		if (!pending || pending.operationId !== operationId) {
			throw new TypeError("The deferred provider operation does not match the pending operation.");
		}
		if (!this.deferredResponses) throw new Error("A deferred response Adapter is not configured.");
		const reportedTokens = this.restoreReportedTokensBeforeDeferred(pending);
		const input: NormalizedGameInput = Object.freeze({
			sessionId: this.sessionId,
			actorId: this.actorId,
			inputId: pending.input.inputId,
			payload: freezeJsonValue(structuredClone(pending.input.payload)),
			...(pending.input.admissionAuthority === undefined
				? {}
				: { admissionAuthority: captureRunAdmissionAuthority(pending.input.admissionAuthority) }),
		});
		this.agent.state.messages = this.agent.state.messages.filter(
			(message) => message.role !== "assistant" || isCompletedAssistantMessage(message),
		);
		return this.activate(input, pending.coordinate.runId, pending, reportedTokens);
	}

	async abandonDeferred(
		operationId: string,
		afterLocalAbandon?: () => void | Promise<void>,
	): Promise<GameDeferredAbandonResult> {
		assertOpaqueIdentifier(operationId);
		if (this.active || this.deferredTransition) throw new GameAgentBusyError();
		const current = this.pendingDeferred;
		if (!current || current.operationId !== operationId) {
			throw new TypeError("The deferred provider operation does not match the pending operation.");
		}
		this.deferredTransition = true;
		let pending: DurableTranscriptDeferredOpenedRecord;
		try {
			pending = await this.serial.runExclusive(async () => {
				const transcript = this.transcript;
				if (!transcript) throw new TypeError("A durable transcript is required for deferred abandonment.");
				try {
					const committed = await this.commitTranscript(transcript, {
						stream: { sessionId: this.sessionId, actorId: this.actorId },
						expectedRevision: this.transcriptRevision,
						records: [
							{
								type: "deferred.abandoned",
								coordinate: current.coordinate,
								operationId: current.operationId,
								openedRecordId: current.recordId,
								reason: "cancelled",
							},
							{ type: "turn.closed", coordinate: current.coordinate, status: "interrupted" },
							{
								type: "deferred.run.closed",
								coordinate: current.coordinate,
								operationId: current.operationId,
								inputId: current.input.inputId,
								status: "aborted",
							},
						],
					});
					this.transcriptRevision = committed.revision;
					this.pendingDeferred = undefined;
					this.pendingDeferredFinalOutput = undefined;
					return structuredClone(current);
				} catch (error) {
					this.transcriptStateUsable = false;
					throw error;
				}
			});

			await this.emitAt(pending.coordinate, { type: "run.completed", status: "aborted" });
		} finally {
			this.deferredTransition = false;
		}
		await afterLocalAbandon?.();

		let providerCancellation: GameDeferredAbandonResult["providerCancellation"] = "unavailable";
		if (this.deferredResponses) {
			try {
				await this.deferredResponses.cancel(pending.handle, new AbortController().signal);
				providerCancellation = "completed";
			} catch {
				providerCancellation = "failed";
			}
		}
		return { operationId, providerCancellation };
	}

	control(expected: RunCoordinate, command: GameControlCommand): Promise<GameControlResult> {
		expected = { sessionId: expected.sessionId, actorId: expected.actorId, runId: expected.runId, turn: expected.turn };
		const type = command.type;
		command = type === "abort" ? { type, reason: command.reason } : { type, text: command.text };
		return this.serial.runExclusive(async () => {
			const active = this.active;
			if (!active) {
				return { accepted: false, reason: "no-active-run" };
			}
			const coordinate = this.coordinate(active);
			if (!sameCoordinate(expected, coordinate)) {
				return { accepted: false, reason: "coordinate-mismatch", activeCoordinate: coordinate };
			}
			if (!active.controlOpen) {
				return { accepted: false, reason: "run-not-active", activeCoordinate: coordinate };
			}
			if (command.type !== "abort" && command.text.length === 0) {
				return { accepted: false, reason: "invalid-message", activeCoordinate: coordinate };
			}

			await this.emit(active, { type: "control.accepted", control: command.type });
			switch (command.type) {
				case "abort":
					return this.abortAccepted(active, coordinate, command.reason);
				case "steer": {
					const message = { role: "user" as const, content: command.text, timestamp: this.now() };
					active.repeatResetMessages.add(message);
					this.agent.steer(message);
					break;
				}
				case "follow-up": {
					const message = { role: "user" as const, content: command.text, timestamp: this.now() };
					active.repeatResetMessages.add(message);
					if (active.exactToolRepeat?.isTerminated()) {
						this.agent.steer(message);
					} else {
						active.pendingFollowUpMessages.add(message);
						this.agent.followUp(message);
					}
					break;
				}
			}
			return { accepted: true, coordinate };
		});
	}

	abortRun(
		expected: Readonly<Pick<RunCoordinate, "sessionId" | "actorId" | "runId">>,
		reason?: unknown,
	): Promise<GameControlResult> {
		expected = { sessionId: expected.sessionId, actorId: expected.actorId, runId: expected.runId };
		return this.serial.runExclusive(async () => {
			const active = this.active;
			if (!active) return { accepted: false, reason: "no-active-run" };
			const coordinate = this.coordinate(active);
			if (
				expected.sessionId !== coordinate.sessionId ||
				expected.actorId !== coordinate.actorId ||
				expected.runId !== coordinate.runId
			) {
				return { accepted: false, reason: "coordinate-mismatch", activeCoordinate: coordinate };
			}
			if (!active.controlOpen) {
				return { accepted: false, reason: "run-not-active", activeCoordinate: coordinate };
			}

			await this.emit(active, { type: "control.accepted", control: "abort" });
			return this.abortAccepted(active, coordinate, reason);
		});
	}

	private abortAccepted(active: ActiveRun, coordinate: RunCoordinate, reason: unknown): GameControlResult {
		active.abortRequested = true;
		active.controlOpen = false;
		active.abortController.abort(reason);
		this.agent.abort();
		return { accepted: true, coordinate };
	}

	private activate(
		input: NormalizedGameInput,
		runId: string,
		deferredResume?: DurableTranscriptDeferredOpenedRecord,
		reportedTokens = 0,
	): GameRunHandle {
		const metrics = new RunMetricsTracker(this.metricsNow);
		if (this.active || this.deferredTransition) throw new GameAgentBusyError();
		const projectorInitialTurn = deferredResume ? BigInt(deferredResume.coordinate.turn) - 1n : 0n;
		const activeTurn = deferredResume ? BigInt(deferredResume.coordinate.turn) : 0n;
		const initialFinalOutput = deferredResume ? this.pendingDeferredFinalOutput : undefined;
		if (deferredResume) this.pendingDeferredFinalOutput = undefined;
		const abortController = new AbortController();
		this.agent.clearAllQueues();
		let active: ActiveRun;
		const projector = new PiEventProjector({
			initialTurn: projectorInitialTurn,
			...(initialFinalOutput === undefined ? {} : { initialFinalOutput }),
			newId: this.newId,
			setTurn: (turn) => {
				active.turn = turn;
			},
			emit: async (data) => {
				active.metrics.observe(data);
				await this.emit(active, data);
			},
		});
		active = {
			runId,
			turn: activeTurn,
			input,
			abortController,
			abortRequested: false,
			controlOpen: true,
			stopPolicyFailed: false,
			reportedTokens,
			reportedTokenAdmission: "open",
			transcriptCompactionFailed: false,
			transcriptPersistenceFailed: false,
			providerProvenanceFailed: false,
			nextTranscriptContextMessages: undefined,
			projector,
			transcriptTurnOpen: deferredResume !== undefined,
			transcriptToolIntents: [],
			...(deferredResume ? { deferredResume } : {}),
			deferredFetchPending: deferredResume !== undefined,
			providerAttempt: undefined,
			exactToolRepeat: this.exactToolRepeatPolicy ? new ExactToolRepeatTracker(this.exactToolRepeatPolicy) : undefined,
			exactToolRepeatAdvisory: undefined,
			repeatPolicyFailed: false,
			repeatResetMessages: new Set(),
			pendingFollowUpMessages: new Set(),
			metrics,
		} satisfies ActiveRun;
		this.active = active;
		const initialCoordinate = deferredResume ? structuredClone(deferredResume.coordinate) : this.coordinate(active);
		return { runId, initialCoordinate, completion: withRunMetrics(active.metrics, () => this.execute(active)) };
	}

	private async execute(active: ActiveRun): Promise<GameRunResult> {
		let status: GameRunResult["status"] = "failed";
		let terminalEmitted = false;
		let metricsEmitted = false;
		let committedDeferredTerminalStatus: "completed" | "aborted" | "failed" | undefined;
		try {
			await this.serial.runExclusive(() =>
				this.emit(
					active,
					active.deferredResume
						? { type: "run.resumed", operationId: active.deferredResume.operationId }
						: { type: "run.started", inputId: active.input.inputId },
				),
			);
			if (active.abortRequested) {
				status = "aborted";
			} else if (active.deferredResume) {
				await this.agent.continue();
				status = this.runCompletionStatus(active);
			} else {
				const rawProjection = this.projectInput
					? await this.projectInput(active.input, active.abortController.signal, this.coordinate(active))
					: defaultGameInputProjection(active.input, this.now());
				active.abortController.signal.throwIfAborted();
				const projected = this.inputProjectionLimits
					? validateGameInputProjection(
							rawProjection,
							this.inputProjectionLimits,
							this.agent.state.model.input.includes("image"),
						)
					: rawProjection;
				active.abortController.signal.throwIfAborted();
				if (this.transcript) {
					const durableUser = captureTranscriptMessage(projected.message, projected.transcriptContent);
					if (durableUser?.role !== "user") {
						throw new TypeError("The projected user message is not durable.");
					}
					this.transcript.assertMediaSupported(durableUser);
					active.nextTranscriptUserMessage = durableUser;
				}
				active.projector.setNextUserContent(projected.transcriptContent);
				active.nextTranscriptUserContent = projected.transcriptContent;
				await this.agent.prompt(projected.message);
				status = this.runCompletionStatus(active);
				while (status === "completed" && (await this.reopenForQueuedControl(active))) {
					await this.agent.continue();
					status = this.runCompletionStatus(active);
				}
			}

			const pending = this.pendingFor(active);
			if (pending && status !== "failed") {
				status = "deferred";
				await this.serial.runExclusive(async () => {
					await this.emit(active, active.metrics.snapshot("deferred"));
					metricsEmitted = true;
					await this.emit(active, { type: "run.suspended", operationId: pending.operationId });
				});
			} else {
				if (status === "deferred") throw new TypeError("The deferred provider operation is unavailable.");
				const terminalStatus = status;
				if (!pending && (await this.recordDeferredRunClosed(active, terminalStatus))) {
					committedDeferredTerminalStatus = terminalStatus;
				}
				await this.serial.runExclusive(async () => {
					await this.emit(active, active.metrics.snapshot(terminalStatus));
					metricsEmitted = true;
					await this.emit(active, { type: "run.completed", status: terminalStatus });
				});
			}
			terminalEmitted = true;
		} catch {
			active.controlOpen = false;
			const pending = this.pendingFor(active);
			status =
				pending && !active.providerProvenanceFailed && !active.transcriptPersistenceFailed
					? "deferred"
					: (committedDeferredTerminalStatus ?? (active.abortController.signal.aborted ? "aborted" : "failed"));
			if (!terminalEmitted) {
				try {
					await this.serial.runExclusive(async () => {
						if (!metricsEmitted) {
							await this.emit(active, active.metrics.snapshot(status));
							metricsEmitted = true;
						}
						await this.emit(
							active,
							pending && status === "deferred"
								? { type: "run.suspended", operationId: pending.operationId }
								: { type: "run.completed", status: status as "completed" | "aborted" | "failed" },
						);
					});
				} catch {
					// The event sink is authoritative. If it is unavailable, do not fabricate settlement.
				}
			}
		} finally {
			await this.serial.runExclusive(() => {
				active.controlOpen = false;
				active.projector.clearPendingUserContent();
				this.agent.clearAllQueues();
				if (this.active === active) {
					this.active = undefined;
				}
			});
		}
		const pending = this.pendingFor(active);
		if (status === "deferred" && pending) {
			this.pendingDeferredFinalOutput = active.projector.readFinalOutput();
			return { runId: active.runId, status, operationId: pending.operationId };
		}
		if (status === "completed") {
			const finalOutput = active.projector.readFinalOutput();
			return { runId: active.runId, status, ...(finalOutput === undefined ? {} : { finalOutput }) };
		}
		return { runId: active.runId, status: status as "aborted" | "failed" };
	}

	private runCompletionStatus(active: ActiveRun): GameRunResult["status"] {
		if (
			active.providerProvenanceFailed ||
			active.transcriptPersistenceFailed ||
			active.repeatPolicyFailed ||
			active.reportedTokenAdmission === "failed"
		) {
			return "failed";
		}
		if (this.pendingFor(active)) return "deferred";
		if (active.abortRequested) return "aborted";
		if (active.transcriptCompactionFailed) return "failed";
		return active.projector.completionStatus(active.stopPolicyFailed);
	}

	private captureReportedTokens(active: ActiveRun, message: Readonly<AssistantMessage>): void {
		const limit = this.maxReportedTokensPerRun;
		if (limit === undefined || active.reportedTokenAdmission !== "open" || !isCompletedAssistantMessage(message)) {
			return;
		}
		const usage: unknown = (message as unknown as { readonly usage?: unknown }).usage;
		const totalTokens =
			typeof usage === "object" && usage !== null
				? (usage as { readonly totalTokens?: unknown }).totalTokens
				: undefined;
		if (typeof totalTokens !== "number" || !Number.isSafeInteger(totalTokens) || totalTokens <= 0) {
			active.reportedTokenAdmission = "failed";
			return;
		}
		const next = active.reportedTokens + totalTokens;
		if (!Number.isSafeInteger(next)) {
			active.reportedTokenAdmission = "failed";
			return;
		}
		active.reportedTokens = next;
		if (next >= limit) active.reportedTokenAdmission = "closed";
	}

	private restoreReportedTokensBeforeDeferred(pending: DurableTranscriptDeferredOpenedRecord): number {
		const limit = this.maxReportedTokensPerRun;
		if (limit === undefined) return 0;
		const reportedTokens = pending.reportedTokensBeforeDeferred;
		if (typeof reportedTokens !== "number" || !Number.isSafeInteger(reportedTokens) || reportedTokens < 0) {
			throw new TypeError("The deferred run does not contain valid cumulative reported-token accounting.");
		}
		if (reportedTokens >= limit) {
			throw new TypeError("The deferred run has already exhausted its reported-token admission limit.");
		}
		return reportedTokens;
	}

	private async applyExactToolRepeatPolicy(
		context: BeforeToolCallContext,
		signal?: AbortSignal,
	): Promise<BeforeToolCallResult | undefined> {
		const active = this.active;
		const tracker = active?.exactToolRepeat;
		if (!active || !tracker) return undefined;
		if (active.repeatPolicyFailed) {
			return {
				block: true,
				terminate: true,
				reason: "Tool dispatch was blocked because exact-repeat policy recording is unavailable.",
			};
		}
		const policySignal = this.combineAbortSignals(signal, active.abortController.signal);
		policySignal?.throwIfAborted();
		const observation = tracker.observe(context.toolCall, context.toolCall.name, context.args);
		if (!observation) return undefined;
		const action = observation.action;
		if (action) {
			try {
				const policyData = {
					type: "tool.repeat.detected" as const,
					toolCallId: context.toolCall.id,
					toolName: context.toolCall.name,
					consecutiveCount: String(observation.consecutiveCount),
					action,
				};
				const { emitted, expectedCoordinate } = await this.serial.runExclusive(async () => {
					policySignal?.throwIfAborted();
					if (this.active !== active) throw new Error("The game run is not active.");
					const expectedCoordinate = this.coordinate(active);
					const emitted = await this.emit(active, policyData);
					return { emitted, expectedCoordinate };
				});
				const capturedEvent = structuredClone(emitted);
				assertCanonicalEvent(capturedEvent);
				const emittedData = capturedEvent.data;
				if (
					!sameCoordinate(capturedEvent.coordinate, expectedCoordinate) ||
					canonicalizeJsonValue(capturedEvent.audience) !== canonicalizeJsonValue(eventAudience(policyData)) ||
					emittedData.type !== "tool.repeat.detected" ||
					emittedData.toolCallId !== context.toolCall.id ||
					emittedData.toolName !== context.toolCall.name ||
					emittedData.consecutiveCount !== String(observation.consecutiveCount) ||
					emittedData.action !== action
				) {
					throw new TypeError("The emitted exact Tool-repeat event does not match the detected call.");
				}
				if (action === "advisory") {
					active.exactToolRepeatAdvisory = Object.freeze({
						eventId: capturedEvent.eventId,
						eventRevision: capturedEvent.revision,
						toolName: context.toolCall.name,
						consecutiveCount: String(observation.consecutiveCount),
					});
				} else {
					this.promotePendingFollowUps(active);
				}
			} catch (error) {
				if (policySignal?.aborted || active.abortController.signal.aborted) {
					policySignal?.throwIfAborted();
					active.abortController.signal.throwIfAborted();
					throw error;
				}
				active.repeatPolicyFailed = true;
				return {
					block: true,
					terminate: true,
					reason: "Tool dispatch was blocked because exact-repeat policy recording is unavailable.",
				};
			}
		}
		if (!observation.block) return undefined;
		return {
			block: true,
			terminate: true,
			reason: `Tool '${context.toolCall.name}' was not dispatched because its prepared call repeated ${observation.consecutiveCount} consecutive times.`,
		};
	}

	private promotePendingFollowUps(active: ActiveRun): void {
		if (active.pendingFollowUpMessages.size === 0) return;
		this.agent.clearFollowUpQueue();
		for (const message of active.pendingFollowUpMessages) this.agent.steer(message);
		active.pendingFollowUpMessages.clear();
	}

	private combineAbortSignals(
		first: AbortSignal | undefined,
		second: AbortSignal | undefined,
	): AbortSignal | undefined {
		if (!first) return second;
		if (!second || first === second) return first;
		return AbortSignal.any([first, second]);
	}

	private pendingFor(active: ActiveRun): DurableTranscriptDeferredOpenedRecord | undefined {
		const pending = this.pendingDeferred;
		return pending?.coordinate.runId === active.runId ? pending : undefined;
	}

	private async streamWithFreshRequestState(
		model: Parameters<StreamFn>[0],
		context: Parameters<StreamFn>[1],
		streamOptions: Parameters<StreamFn>[2],
	): Promise<AssistantMessageEventStream> {
		const active = this.active;
		active?.metrics.beginContext();
		const requestSignal = this.combineAbortSignals(streamOptions?.signal, active?.abortController.signal);
		const signal = requestSignal;
		const requestOptions = requestSignal ? { ...streamOptions, signal: requestSignal } : streamOptions;
		let tools: readonly AgentTool[];
		let snapshot: PreparedGameContextSnapshot | undefined;
		let visibleSnapshot: VisibleToolSnapshot | undefined;
		let loadedContextSources: LoadedGameContextSource[] = [];
		let contextMessageIndex: number | undefined;
		let fetchedDeferredMessage: AssistantMessage | undefined;
		const toolOwners = new Map<string, ModelContextToolOwner>();
		try {
			if (!active || this.active !== active) throw new Error("The game run is not active.");
			if (!this.transcriptStateUsable) {
				throw new Error("The live Agent state no longer matches its durable transcript.");
			}
			if (signal?.aborted) throw signal.reason;
			if (!requestSignal) throw new Error("The game request signal is unavailable.");
			if (!context.tools) throw new TypeError("Pi omitted the mutable Tool snapshot.");
			const isDeferredFetch = active.deferredFetchPending;
			if (isDeferredFetch) {
				active.deferredFetchPending = false;
				const adapter = this.deferredResponses;
				const pending = active.deferredResume;
				if (!adapter || !pending) {
					active.metrics.endContext();
					return preparationFailureStream(model, false, this.now());
				}
				active.metrics.endContext();
				active.metrics.providerCommitted(
					{ provider: pending.handle.provider, api: pending.handle.api, id: pending.handle.modelId },
					"1",
				);
				try {
					fetchedDeferredMessage = await adapter.fetch(pending.handle, requestSignal);
					assertDeferredFetchMessage(pending.handle, fetchedDeferredMessage);
					if (hasProviderResponseContent(fetchedDeferredMessage)) active.metrics.providerResponseObserved();
				} catch {
					return preparationFailureStream(model, requestSignal.aborted, this.now());
				} finally {
					active.metrics.providerSettled();
				}
				if (!fetchedDeferredMessage.content.some((part) => part.type === "toolCall")) {
					active.metrics.endContext();
					return completedAssistantStream(fetchedDeferredMessage);
				}
				active.metrics.beginContext();
			}
			const requestCoordinate = Object.freeze(this.coordinate(active));
			if (!isDeferredFetch) {
				loadedContextSources = [
					...(await loadGameContextSources(this.contextSources, active.input, requestSignal, requestCoordinate)),
				];
			}
			if (!isDeferredFetch && contextContainsImages(context.messages) && !model.input.includes("image")) {
				throw new TypeError("The selected model does not accept image input.");
			}
			const resultTransformers: GameToolResultTransformer[] = [];
			let transformToolResultTextBytes: number | undefined;
			const baseTools = this.toolSource ? await this.toolSource(active.input, requestSignal, requestCoordinate) : [];
			let candidateSnapshot: PreparedToolSnapshot | undefined;
			let policyRevision: string | undefined;
			if (this.toolPolicy && this.toolLimits) {
				candidateSnapshot = prepareToolSnapshot(baseTools, this.toolLimits);
				for (const descriptor of candidateSnapshot.descriptors) toolOwners.set(descriptor.name, { kind: "host" });
				visibleSnapshot = await this.evaluateToolPolicy(active, candidateSnapshot, requestSignal, requestCoordinate);
				policyRevision = visibleSnapshot.policyRevision;
			}
			for (const source of this.requestSources) {
				requestSignal.throwIfAborted();
				if (!candidateSnapshot || !visibleSnapshot) throw new TypeError("The Tool policy snapshot is unavailable.");
				const previousCandidateSnapshot = candidateSnapshot;
				const previousVisibleSnapshot = visibleSnapshot;
				const contribution = captureRequestContribution(
					source.sourceId,
					await source.collect(active.input, requestSignal, requestCoordinate, visibleSnapshot.visibleToolNames),
				);
				const sourceContext = contribution.context ?? { references: [], images: [] };
				const additionalTools = contribution.tools ?? [];
				if (!this.toolLimits) throw new TypeError("Tool snapshot limits are unavailable.");
				if (
					contribution.requiredToolResultBytes !== undefined &&
					contribution.requiredToolResultBytes > this.toolLimits.maxResultBytes
				) {
					throw new InvalidRequestSourceError(
						`Request source ${source.sourceId} requires a larger Tool result admission budget.`,
					);
				}
				if (contribution.transformToolResult) resultTransformers.push(contribution.transformToolResult);
				if (contribution.transformToolResultTextBytes !== undefined) {
					transformToolResultTextBytes = Math.max(
						transformToolResultTextBytes ?? 0,
						contribution.transformToolResultTextBytes,
					);
					try {
						textToolResultContentBytes(transformToolResultTextBytes, this.toolLimits.maxResultContentParts);
					} catch (error) {
						throw new InvalidRequestSourceError(
							`Request source ${source.sourceId} exceeds the host Tool result admission range.`,
							{ cause: error },
						);
					}
				}
				if (!isDeferredFetch) {
					loadedContextSources.push({
						sourceId: source.sourceId,
						provenanceKind: source.provenanceKind ?? "extension",
						references: sourceContext.references,
						images: sourceContext.images ?? [],
					});
				}
				candidateSnapshot = extendPreparedToolSnapshot(candidateSnapshot, additionalTools, this.toolLimits);
				for (const tool of additionalTools) {
					toolOwners.set(tool.name, { kind: "source", sourceId: source.sourceId });
				}
				visibleSnapshot = await this.evaluateToolPolicy(active, candidateSnapshot, requestSignal, requestCoordinate);
				if (visibleSnapshot.policyRevision !== policyRevision) {
					throw new Error("The Tool policy changed during provider request preparation.");
				}
				assertStableToolPolicyExtension(previousCandidateSnapshot, previousVisibleSnapshot, visibleSnapshot);
			}
			if (loadedContextSources.some((source) => source.references.length > 0 || source.images.length > 0)) {
				if (!this.contextLimits) throw new TypeError("Game context limits are not configured.");
				snapshot = prepareLoadedGameContextSnapshot(loadedContextSources, this.contextLimits, requestSignal);
			}
			if (snapshot?.images.length && !model.input.includes("image")) {
				throw new GameContextSnapshotError("context_image_input_unsupported");
			}
			if (candidateSnapshot && visibleSnapshot) {
				const configuredToolLimits = this.toolLimits;
				if (!configuredToolLimits) throw new TypeError("Tool snapshot limits are unavailable.");
				tools = Object.freeze(
					visibleSnapshot.tools.map((tool, index) => {
						const descriptor = visibleSnapshot?.descriptors[index];
						if (!descriptor || !candidateSnapshot || !visibleSnapshot) {
							throw new Error("The Tool snapshot is incomplete.");
						}
						return this.guardTool(
							active,
							tool,
							descriptor.fingerprint,
							candidateSnapshot,
							visibleSnapshot,
							requestCoordinate,
							Object.freeze([...resultTransformers]),
							transformToolResultTextBytes,
						);
					}),
				);
			} else {
				tools = Object.freeze([]);
			}
			if (signal?.aborted) throw signal.reason;
			if (snapshot) await this.recordContextSnapshot(active, snapshot, signal ?? active.abortController.signal);
			if (signal?.aborted) throw signal.reason;
			if (candidateSnapshot && visibleSnapshot) {
				const publicationSnapshot = await this.evaluateToolPolicy(
					active,
					candidateSnapshot,
					requestSignal,
					requestCoordinate,
				);
				if (publicationSnapshot.revision !== visibleSnapshot.revision) {
					throw new Error("The Tool policy changed before provider publication.");
				}
			}
			if (signal?.aborted) throw signal.reason;
		} catch {
			active?.metrics.endContext();
			context.tools?.splice(0);
			this.agent.state.tools = [];
			return preparationFailureStream(model, signal?.aborted === true, this.now());
		}

		context.tools.splice(0, context.tools.length, ...tools);
		this.agent.state.tools = [...tools];
		if (snapshot) {
			const insertionIndex = context.messages.findLastIndex((message) => message.role === "user");
			contextMessageIndex = insertionIndex < 0 ? 0 : insertionIndex;
			context.messages.splice(contextMessageIndex, 0, {
				role: "user",
				content:
					snapshot.images.length === 0
						? snapshot.text
						: [{ type: "text", text: snapshot.text }, ...snapshot.images.map((image) => image.content)],
				timestamp: this.now(),
			});
		}
		if (fetchedDeferredMessage) {
			active.metrics.endContext();
			return completedAssistantStream(fetchedDeferredMessage);
		}
		const provenance = this.providerProvenance;
		const exactToolRepeatAdvisory = provenance ? this.prepareExactToolRepeatAdvisoryProvenance(active) : undefined;
		const requestTemplate = provenance
			? prepareModelRequestProvenanceTemplate({
					requestedModel: model,
					context,
					coordinate: this.coordinate(active),
					transcriptRevision: this.transcriptRevision,
					...(this.transcriptCheckpointRevision === undefined
						? {}
						: { checkpointRevision: this.transcriptCheckpointRevision }),
					provenance,
					...(snapshot ? { contextSnapshot: snapshot } : {}),
					...(contextMessageIndex === undefined ? {} : { contextMessageIndex }),
					loadedContextSources,
					...(visibleSnapshot ? { visibleToolSnapshot: visibleSnapshot } : {}),
					toolOwners,
					...(exactToolRepeatAdvisory ? { exactToolRepeatAdvisory } : {}),
				})
			: undefined;
		active.metrics.endContext();
		const attempts = createProviderAttemptLifecycle({
			...(provenance && requestTemplate ? { sink: provenance.sink, requestTemplate } : {}),
			now: this.now,
			onCommit: (attempt, targetModel) => {
				active.providerAttempt = attempt;
				active.metrics.providerCommitted(targetModel, attempt.index);
			},
			onSettle: (attempt) => {
				if (active.providerAttempt === attempt) active.providerAttempt = undefined;
				active.metrics.providerSettled();
			},
			onResponse: (message) => {
				if (hasProviderResponseContent(message)) active.metrics.providerResponseObserved();
				active.metrics.providerSettled();
			},
			onFailure: () => {
				active.providerProvenanceFailed = true;
				active.providerAttempt = undefined;
				active.metrics.providerSettled();
			},
		});
		if (typeof this.modelStream !== "function") {
			const stream = await this.modelStream.stream(model, context, requestOptions, attempts);
			if (!active.providerAttempt) {
				throw new TypeError("The provider-attempt model stream returned without a committed final attempt.");
			}
			return stream;
		}
		const modelStream = this.modelStream;
		const attempt = attempts.begin(model);
		return await attempt.commit(() => modelStream(model, context, requestOptions));
	}

	private prepareExactToolRepeatAdvisoryProvenance(
		active: ActiveRun,
	): ExactToolRepeatAdvisoryProvenanceInput | undefined {
		const content = active.exactToolRepeat?.modelAdvisory();
		if (!content) return undefined;
		const evidence = active.exactToolRepeatAdvisory;
		if (!evidence) throw new TypeError("The exact Tool-repeat advisory event evidence is unavailable.");
		const expectedContent = exactToolRepeatAdvisoryMessage(evidence.toolName, evidence.consecutiveCount);
		if (content !== expectedContent) throw new TypeError("The exact Tool-repeat advisory event evidence is stale.");
		return Object.freeze({ ...evidence, content });
	}

	private async recordProviderResponse(active: ActiveRun, message: AssistantMessage): Promise<void> {
		const attempt = active.providerAttempt;
		if (!attempt) return;
		try {
			await attempt.settle(message);
		} catch (error) {
			active.providerProvenanceFailed = true;
			active.providerAttempt = undefined;
			throw error;
		}
	}

	private guardTool(
		active: ActiveRun,
		tool: AgentTool,
		descriptorFingerprint: string,
		candidateSnapshot: PreparedToolSnapshot,
		visibleSnapshot: VisibleToolSnapshot,
		requestCoordinate: Readonly<RunCoordinate>,
		resultTransformers: readonly GameToolResultTransformer[],
		transformToolResultTextBytes: number | undefined,
	): AgentTool {
		const execute = tool.execute;
		const guard = this.beforeToolExecution;
		const limits = this.toolLimits;
		if (!limits) throw new TypeError("Tool snapshot limits are unavailable.");
		const prepareArguments = tool.prepareArguments;
		const prepareDurableExecution = (tool as DurableExecutionAgentTool)[DURABLE_TOOL_EXECUTION];
		const recheckPolicy = async (signal: AbortSignal): Promise<VisibleToolSnapshot> => {
			try {
				return await this.evaluateToolPolicy(active, candidateSnapshot, signal, requestCoordinate);
			} catch (error) {
				throw new GameToolResultError("tool_policy_changed", { cause: error });
			}
		};
		const admitArguments = (parameters: unknown): JsonValue =>
			normalizeJsonValueWithin(parameters, {
				maxBytes: limits.maxArgumentsBytes,
				maxDepth: limits.maxArgumentsDepth,
			});
		return Object.freeze({
			name: tool.name,
			label: tool.label,
			description: tool.description,
			parameters: tool.parameters,
			...(tool.executionMode ? { executionMode: tool.executionMode } : {}),
			prepareArguments: (parameters: unknown) => {
				try {
					const admitted = admitArguments(parameters);
					const prepared = prepareArguments ? prepareArguments(admitted) : admitted;
					return freezeJsonValue(admitArguments(prepared)) as never;
				} catch (error) {
					throw new GameToolResultError("tool_arguments_invalid", { cause: error });
				}
			},
			execute: async (
				toolCallId: Parameters<AgentTool["execute"]>[0],
				parameters: Parameters<AgentTool["execute"]>[1],
				signal: Parameters<AgentTool["execute"]>[2],
				onUpdate: Parameters<AgentTool["execute"]>[3],
			) => {
				if (active.repeatPolicyFailed) throw new GameToolResultError("tool_execution_denied");
				const executionSignal = this.combineAbortSignals(signal, active.abortController.signal);
				if (!executionSignal) throw new GameToolResultError("tool_run_inactive");
				if (this.active !== active || active.abortRequested) throw new GameToolResultError("tool_run_inactive");
				executionSignal.throwIfAborted();
				const argumentsValue = freezeJsonValue(admitArguments(parameters));
				const executionArguments = argumentsValue as Readonly<Record<string, JsonValue>>;
				const execution = Object.freeze({
					coordinate: Object.freeze(this.coordinate(active)),
					toolCallId,
					toolName: tool.name,
					arguments: executionArguments,
					descriptorFingerprint,
					catalogRevision: visibleSnapshot.revision,
					policyRevision: visibleSnapshot.policyRevision,
				});
				let preparedOperationId: string | undefined;
				let executePrepared: (onUpdate?: Parameters<AgentTool["execute"]>[3]) => Promise<AgentToolResult<unknown>>;
				if (prepareDurableExecution) {
					const preparedExecution = await prepareDurableExecution(toolCallId, argumentsValue as never, executionSignal);
					const preparedExecute = preparedExecution?.execute;
					if (
						typeof preparedExecution !== "object" ||
						preparedExecution === null ||
						typeof preparedExecute !== "function"
					) {
						throw new TypeError("The durable Tool execution hook returned an invalid preparation.");
					}
					preparedOperationId = preparedExecution.operationId;
					executePrepared = preparedExecute.bind(preparedExecution);
				} else {
					executePrepared = (preparedUpdate) =>
						execute(toolCallId, argumentsValue as never, executionSignal, preparedUpdate);
				}
				executionSignal.throwIfAborted();
				const currentPolicy = await recheckPolicy(executionSignal);
				if (currentPolicy.revision !== visibleSnapshot.revision) {
					throw new GameToolResultError("tool_policy_changed");
				}
				if (guard) {
					try {
						await guard(active.input, execution, executionSignal);
					} catch (error) {
						throw new GameToolResultError("tool_execution_denied", { cause: error });
					}
					const postGuardPolicy = await recheckPolicy(executionSignal);
					if (postGuardPolicy.revision !== visibleSnapshot.revision) {
						throw new GameToolResultError("tool_policy_changed");
					}
				}
				executionSignal.throwIfAborted();
				if (this.active !== active || active.abortRequested) throw new GameToolResultError("tool_run_inactive");
				let acceptingUpdates = true;
				let updateCount = 0;
				const guardedUpdate: NonNullable<Parameters<AgentTool["execute"]>[3]> = (partialResult) => {
					if (!acceptingUpdates) return;
					updateCount += 1;
					if (updateCount > limits.maxResultUpdates) return;
					try {
						onUpdate?.(prepareToolResultSnapshot(partialResult, limits) as never);
					} catch {
						// Supplemental progress is best-effort and cannot poison the final Tool result.
					}
				};
				try {
					await this.recordTranscriptToolStarted(active, execution, preparedOperationId);
					const authority = runtimeGameAgentAuthority(this);
					if (authority) await authority.assertRunAuthorityOpen(execution.coordinate);
					await this.serial.runExclusive(() => {
						executionSignal.throwIfAborted();
						if (this.active !== active || active.abortRequested) {
							throw new GameToolResultError("tool_run_inactive");
						}
					});
					let result: AgentToolResult<unknown>;
					let reportedError = false;
					try {
						result = await executePrepared(guardedUpdate);
					} catch (error) {
						if (!(error instanceof GameToolReportedError)) throw error;
						reportedError = true;
						result = error.result;
					}
					acceptingUpdates = false;
					const snapshot =
						transformToolResultTextBytes === undefined
							? prepareToolResultSnapshot(result, limits)
							: prepareTransformableToolResultSnapshot(result, limits, transformToolResultTextBytes);
					if (this.transcript || reportedError || resultTransformers.length > 0) {
						this.pendingToolResults.set(snapshot, {
							context: Object.freeze({ input: active.input, ...execution }),
							reportedError,
							signal: executionSignal,
							transformers: resultTransformers,
							requiresFinalAdmission: transformToolResultTextBytes !== undefined,
						});
					}
					return snapshot as never;
				} catch (error) {
					acceptingUpdates = false;
					if (
						preparedOperationId !== undefined &&
						(executionSignal.aborted || error instanceof DurableToolExecutionUnresolvedError)
					) {
						const intent = active.transcriptToolIntents.find(
							(candidate) => candidate.call.id === execution.toolCallId && candidate.call.name === execution.toolName,
						);
						if (intent?.startRecordId) intent.executionUnresolved = true;
					}
					throw boundedToolExecutionError(error, limits.maxResultBytes);
				} finally {
					acceptingUpdates = false;
				}
			},
		});
	}

	private async finalizeToolResult(
		context: AfterToolCallContext,
		signal?: AbortSignal,
	): Promise<AfterToolCallResult | undefined> {
		const pending = this.pendingToolResults.get(context.result);
		if (!pending) return undefined;
		this.pendingToolResults.delete(context.result);
		const limits = this.toolLimits;
		if (!limits) return pending.reportedError ? { isError: true } : undefined;
		const finalizationSignal = this.combineAbortSignals(signal, pending.signal);
		if (!finalizationSignal) throw new GameToolResultError("tool_run_inactive");
		let result = context.result;
		let changed = false;
		try {
			finalizationSignal.throwIfAborted();
			const transformContext = Object.freeze({
				...pending.context,
				isError: context.isError || pending.reportedError,
			});
			for (const transformer of pending.transformers) {
				finalizationSignal.throwIfAborted();
				const content = await transformer(result, transformContext, finalizationSignal);
				if (content === undefined) continue;
				result = prepareToolResultSnapshot({ ...result, content }, limits);
				changed = true;
			}
			if (pending.requiresFinalAdmission && !changed) prepareToolResultSnapshot(result, limits);
			if (this.transcript && result.content.some((part) => part.type === "image")) {
				const prepared = await this.transcript.captureToolResult(
					{
						stream: { sessionId: this.sessionId, actorId: this.actorId },
						coordinate: pending.context.coordinate,
						toolCallId: pending.context.toolCallId,
						toolName: pending.context.toolName,
						content: result.content,
					},
					finalizationSignal,
				);
				result = prepareToolResultSnapshot({ ...result, content: prepared.content }, limits);
				const active = this.active;
				const intent = active?.transcriptToolIntents.find(
					(candidate) =>
						candidate.call.id === pending.context.toolCallId && candidate.call.name === pending.context.toolName,
				);
				if (!active || !intent) throw new Error("The durable Tool intent is unavailable during media capture.");
				intent.durableResultContent = prepared.durableContent;
				changed = true;
			}
			finalizationSignal.throwIfAborted();
		} catch (error) {
			const active = this.active;
			const intent = active?.transcriptToolIntents.find(
				(candidate) =>
					candidate.call.id === pending.context.toolCallId && candidate.call.name === pending.context.toolName,
			);
			if (intent?.startRecordId) intent.finalizationFailed = true;
			throw boundedToolExecutionError(error, limits.maxResultBytes);
		}
		return {
			...(changed ? { content: result.content } : {}),
			...(pending.reportedError ? { isError: true } : {}),
		};
	}

	private async evaluateToolPolicy(
		active: ActiveRun,
		candidateSnapshot: PreparedToolSnapshot,
		signal: AbortSignal,
		requestCoordinate: Readonly<RunCoordinate>,
	): Promise<VisibleToolSnapshot> {
		const policy = this.toolPolicy;
		if (!policy) throw new TypeError("The Tool policy is unavailable.");
		signal.throwIfAborted();
		const decision = await policy(active.input, candidateSnapshot.descriptors, signal, requestCoordinate);
		signal.throwIfAborted();
		const limits = this.toolLimits;
		if (!limits) throw new TypeError("Tool snapshot limits are unavailable.");
		return selectVisibleToolSnapshot(candidateSnapshot, decision, limits.maxPolicyRevisionBytes);
	}

	private recordContextSnapshot(
		active: ActiveRun,
		snapshot: PreparedGameContextSnapshot,
		signal: AbortSignal,
	): Promise<void> {
		return this.serial.runExclusive(async () => {
			if (this.active !== active) throw new Error("The game run is not active.");
			signal.throwIfAborted();
			const item = {
				itemType: "message" as const,
				itemId: this.newId(),
				role: "user" as const,
				content: [{ type: "text" as const, text: snapshot.text }, ...snapshot.images.map((image) => image.reference)],
				audience: { visibility: "internal" as const },
				context: snapshot.provenance,
			};
			await this.emit(active, { type: "item.started", item });
			await this.emit(active, { type: "item.completed", item });
		});
	}

	private async recordTranscriptMessage(active: ActiveRun, message: Message): Promise<void> {
		const transcript = this.transcript;
		if (message.role === "assistant" && message.stopReason === "deferred" && !transcript) {
			throw new TranscriptUnsupportedDeferredError();
		}
		if (!transcript) return;
		if (
			message.role === "assistant" &&
			(message.stopReason === "deferred" || (active.deferredResume && this.pendingFor(active)))
		) {
			await this.recordDeferredAssistant(active, message);
			return;
		}
		const projectedUserContent = message.role === "user" ? active.nextTranscriptUserContent : undefined;
		try {
			const toolIntent =
				message.role === "toolResult"
					? active.transcriptToolIntents.find((candidate) => !candidate.completed)
					: undefined;
			if (message.role === "toolResult") {
				if (!toolIntent || toolIntent.call.id !== message.toolCallId || toolIntent.call.name !== message.toolName) {
					throw new TypeError("The Pi Tool result does not match the next durable assistant call.");
				}
				if (toolIntent.executionUnresolved) return;
				if (toolIntent.finalizationFailed) {
					throw new Error("The durable Tool result could not be finalized.");
				}
			}
			const durable =
				message.role === "user" && active.nextTranscriptUserMessage
					? active.nextTranscriptUserMessage
					: captureTranscriptMessage(
							message,
							message.role === "toolResult" ? toolIntent?.durableResultContent : projectedUserContent,
						);
			if (message.role === "user") {
				delete active.nextTranscriptUserContent;
				delete active.nextTranscriptUserMessage;
			}
			if (!durable) {
				if (
					message.role === "assistant" &&
					(message.stopReason === "error" || message.stopReason === "aborted") &&
					message.content.every((part) => part.type === "text" && part.text.length === 0)
				) {
					return;
				}
				this.transcriptStateUsable = false;
				return;
			}
			if (durable.role === "toolResult") {
				const intent = toolIntent;
				if (!intent) throw new TypeError("The durable Tool intent is unavailable.");
				const committed = await this.commitTranscript(transcript, {
					stream: { sessionId: this.sessionId, actorId: this.actorId },
					expectedRevision: this.transcriptRevision,
					records: [
						{
							type: "tool.completed",
							coordinate: this.coordinate(active),
							assistantRecordId: intent.assistantRecordId,
							...(intent.startRecordId ? { startedRecordId: intent.startRecordId } : {}),
							sourceIndex: intent.sourceIndex,
							outcome: intent.startRecordId ? "completed" : "not_started",
							message: durable,
						},
					],
				});
				this.transcriptRevision = committed.revision;
				this.durableTranscriptMessages.push(structuredClone(durable));
				intent.completed = true;
				delete intent.durableResultContent;
				return;
			}
			const committed = await this.commitTranscript(transcript, {
				stream: { sessionId: this.sessionId, actorId: this.actorId },
				expectedRevision: this.transcriptRevision,
				records: [
					{
						type: "message.completed",
						coordinate: this.coordinate(active),
						message: durable,
					},
				],
			});
			this.transcriptRevision = committed.revision;
			this.durableTranscriptMessages.push(structuredClone(durable));
			active.transcriptTurnOpen = true;
			if (durable.role === "assistant") {
				const assistantRecord = committed.records[0] as DurableTranscriptMessageCompletedRecord & {
					message: DurableTranscriptAssistantMessage;
				};
				this.setTranscriptAssistantIntents(active, assistantRecord.recordId, assistantRecord.message);
			}
		} catch (error) {
			this.transcriptStateUsable = false;
			active.transcriptPersistenceFailed = true;
			throw error;
		}
	}

	private async recordDeferredRunClosed(
		active: ActiveRun,
		status: "completed" | "aborted" | "failed",
	): Promise<boolean> {
		const transcript = this.transcript;
		const origin = active.deferredResume;
		if (!transcript || !origin) return false;
		try {
			const committed = await this.commitTranscript(transcript, {
				stream: { sessionId: this.sessionId, actorId: this.actorId },
				expectedRevision: this.transcriptRevision,
				records: [
					{
						type: "deferred.run.closed",
						coordinate: this.coordinate(active),
						operationId: origin.operationId,
						inputId: origin.input.inputId,
						status,
					},
				],
			});
			this.transcriptRevision = committed.revision;
			return true;
		} catch (error) {
			this.transcriptStateUsable = false;
			throw error;
		}
	}

	private async recordDeferredAssistant(active: ActiveRun, message: AssistantMessage): Promise<void> {
		const transcript = this.transcript;
		if (!transcript) return;
		if (message.stopReason === "error" || message.stopReason === "aborted") return;
		const resumed = active.deferredResume;
		const pending = this.pendingFor(active);
		if (message.stopReason === "deferred") {
			const handle = message.deferred;
			if (!handle) throw new TypeError("Pi returned deferred output without a provider handle.");
			const finalOutput = assistantMessageText(message);
			if (pending) {
				if (!resumed || resumed.recordId !== pending.recordId || !sameDeferredHandle(pending.handle, handle)) {
					throw new TypeError("Pi returned a different deferred provider handle.");
				}
				return;
			}
			if (this.pendingDeferred) throw new GameAgentDeferredPendingError(this.pendingDeferred.operationId);
			const operationId = this.newId();
			assertOpaqueIdentifier(operationId);
			try {
				const committed = await this.commitTranscript(transcript, {
					stream: { sessionId: this.sessionId, actorId: this.actorId },
					expectedRevision: this.transcriptRevision,
					records: [
						{
							type: "deferred.opened",
							coordinate: this.coordinate(active),
							operationId,
							input: {
								inputId: active.input.inputId,
								payload: active.input.payload,
								...(active.input.admissionAuthority === undefined
									? {}
									: { admissionAuthority: active.input.admissionAuthority }),
							},
							handle: captureDeferredHandle(handle),
							...(finalOutput === undefined ? {} : { finalOutput }),
							...(this.maxReportedTokensPerRun === undefined
								? {}
								: { reportedTokensBeforeDeferred: active.reportedTokens }),
						},
					],
				});
				const opened = committed.records[0];
				if (opened.type !== "deferred.opened") {
					throw new TypeError("The deferred provider opening was not committed.");
				}
				this.transcriptRevision = committed.revision;
				this.pendingDeferred = opened;
				active.deferredResume = opened;
				active.transcriptTurnOpen = true;
				return;
			} catch (error) {
				this.transcriptStateUsable = false;
				throw error;
			}
		}

		if (!resumed || !pending || resumed.recordId !== pending.recordId) {
			throw new TypeError("The deferred provider completion has no matching pending operation.");
		}
		if (
			message.api !== pending.handle.api ||
			message.provider !== pending.handle.provider ||
			message.model !== pending.handle.modelId
		) {
			throw new TypeError("The deferred provider completion does not match its provider origin.");
		}
		const durable = captureTranscriptMessage(message);
		if (durable?.role !== "assistant") {
			throw new TypeError("The deferred provider completion is not a durable assistant message.");
		}
		try {
			const committed = await this.commitTranscript(transcript, {
				stream: { sessionId: this.sessionId, actorId: this.actorId },
				expectedRevision: this.transcriptRevision,
				records: [
					{
						type: "deferred.completed",
						coordinate: pending.coordinate,
						operationId: pending.operationId,
						openedRecordId: pending.recordId,
						message: durable,
					},
				],
			});
			const completed = committed.records[0];
			if (completed.type !== "deferred.completed") {
				throw new TypeError("The deferred provider completion was not committed.");
			}
			this.transcriptRevision = committed.revision;
			this.durableTranscriptMessages.push(structuredClone(durable));
			this.pendingDeferred = undefined;
			this.setTranscriptAssistantIntents(active, completed.recordId, completed.message);
		} catch (error) {
			this.transcriptStateUsable = false;
			throw error;
		}
	}

	private setTranscriptAssistantIntents(
		active: ActiveRun,
		assistantRecordId: string,
		message: DurableTranscriptAssistantMessage,
	): void {
		active.transcriptToolIntents = message.content.flatMap((content, sourceIndex) =>
			content.type === "toolCall" ? [{ assistantRecordId, sourceIndex, call: content, completed: false }] : [],
		);
	}

	private async recordTranscriptToolStarted(
		active: ActiveRun,
		execution: Readonly<GameToolExecution>,
		operationId: string | undefined,
	): Promise<void> {
		const transcript = this.transcript;
		if (!transcript) return;
		if (!active.transcriptTurnOpen) throw new TypeError("The durable Tool turn is not open.");
		await this.serial.runExclusive(async () => {
			if (this.active !== active || active.abortRequested) throw new GameToolResultError("tool_run_inactive");
			const intent = active.transcriptToolIntents.find(
				(candidate) =>
					candidate.startRecordId === undefined &&
					!candidate.completed &&
					candidate.call.id === execution.toolCallId &&
					candidate.call.name === execution.toolName,
			);
			if (!intent) throw new TypeError("The Tool execution does not match a durable assistant call.");
			try {
				const committed = await this.commitTranscript(transcript, {
					stream: { sessionId: this.sessionId, actorId: this.actorId },
					expectedRevision: this.transcriptRevision,
					records: [
						{
							type: "tool.started",
							coordinate: this.coordinate(active),
							assistantRecordId: intent.assistantRecordId,
							sourceIndex: intent.sourceIndex,
							toolCallId: execution.toolCallId,
							toolName: execution.toolName,
							arguments: execution.arguments,
							descriptorFingerprint: execution.descriptorFingerprint,
							catalogRevision: execution.catalogRevision,
							policyRevision: execution.policyRevision,
							...(operationId === undefined ? {} : { operationId }),
						},
					],
				});
				this.transcriptRevision = committed.revision;
				intent.startRecordId = committed.records[0].recordId;
			} catch (error) {
				this.transcriptStateUsable = false;
				throw error;
			}
		});
	}

	private async recordTranscriptTurnClosed(active: ActiveRun, message: AgentMessage): Promise<void> {
		const transcript = this.transcript;
		if (!transcript) return;
		if (!active.transcriptTurnOpen) return;
		if (this.pendingFor(active)) return;
		const interrupted =
			active.abortRequested ||
			(isPiMessage(message) &&
				message.role === "assistant" &&
				(message.stopReason === "error" || message.stopReason === "aborted"));
		if (active.transcriptToolIntents.some((intent) => !intent.completed)) {
			this.transcriptStateUsable = false;
			if (interrupted) return;
			throw new TypeError("Pi closed a durable turn with unanswered Tool calls.");
		}
		let closure: DurableTranscriptTurnClosedRecord;
		try {
			const committed = await this.commitTranscript(transcript, {
				stream: { sessionId: this.sessionId, actorId: this.actorId },
				expectedRevision: this.transcriptRevision,
				records: [
					{
						type: "turn.closed",
						coordinate: this.coordinate(active),
						status: interrupted ? "interrupted" : "completed",
					},
				],
			});
			this.transcriptRevision = committed.revision;
			active.transcriptTurnOpen = false;
			active.transcriptToolIntents = [];
			const committedClosure = committed.records[0];
			if (committedClosure.type !== "turn.closed") {
				throw new TypeError("The durable transcript closure was not committed.");
			}
			closure = committedClosure;
		} catch (error) {
			this.transcriptStateUsable = false;
			throw error;
		}
		if (closure.status === "completed" && this.transcriptCompactor) {
			try {
				await this.compactTranscript(active, closure);
			} catch {
				active.transcriptCompactionFailed = true;
			}
		}
	}

	private async compactTranscript(
		active: ActiveRun,
		source: Readonly<DurableTranscriptTurnClosedRecord>,
	): Promise<void> {
		const transcript = this.transcript;
		const compactor = this.transcriptCompactor;
		if (!transcript || !compactor) return;
		const durableMessages = structuredClone(this.durableTranscriptMessages);
		const providerMessages = await transcript.materialize(
			{ sessionId: this.sessionId, actorId: this.actorId },
			durableMessages,
			active.abortController.signal,
		);
		const plan = await compactor.prepare({
			model: this.agent.state.model,
			systemPrompt: this.agent.state.systemPrompt,
			tools: this.agent.state.tools.map((tool) => ({
				name: tool.name,
				description: tool.description,
				parameters: normalizeJsonValue(tool.parameters),
			})),
			messages: durableMessages,
			providerMessages,
			signal: active.abortController.signal,
		});
		if (!plan) return;
		let checkpoint: TranscriptCheckpointResult;
		try {
			checkpoint = await transcript.checkpoint({
				stream: { sessionId: this.sessionId, actorId: this.actorId },
				source,
				replacement: plan.replacement,
				signal: active.abortController.signal,
			});
		} catch (error) {
			if (error instanceof TranscriptCheckpointCommitError) this.transcriptStateUsable = false;
			throw error;
		}
		this.transcriptRevision = checkpoint.revision;
		this.transcriptCheckpointRevision = checkpoint.revision;
		try {
			const messages = [...checkpoint.messages];
			this.durableTranscriptMessages = [...structuredClone(checkpoint.durableMessages)];
			active.nextTranscriptContextMessages = messages;
			this.agent.state.messages = messages;
		} catch (error) {
			this.transcriptStateUsable = false;
			throw error;
		}
	}

	private reopenForQueuedControl(active: ActiveRun): Promise<boolean> {
		return this.serial.runExclusive(() => {
			if (
				this.active !== active ||
				active.abortRequested ||
				active.reportedTokenAdmission !== "open" ||
				!this.agent.hasQueuedMessages()
			) {
				return false;
			}
			active.controlOpen = true;
			return true;
		});
	}

	private async emit(active: ActiveRun, data: CanonicalEventData): Promise<CanonicalEvent> {
		const startedAt = active.metrics.beginDurable();
		try {
			return await this.emitAt(this.coordinate(active), data);
		} finally {
			active.metrics.endDurable(startedAt);
		}
	}

	private async commitTranscript(transcript: DurableTranscript, request: Parameters<DurableTranscript["commit"]>[0]) {
		const metrics = this.active?.metrics;
		const startedAt = metrics?.beginDurable();
		try {
			return await transcript.commit(request);
		} finally {
			if (metrics && startedAt !== undefined) metrics.endDurable(startedAt);
		}
	}

	private async emitAt(coordinate: RunCoordinate, data: CanonicalEventData): Promise<CanonicalEvent> {
		assertCanonicalEventData(data);
		const event = await this.emitEvent(coordinate, data);
		for (const [listener, boundary] of this.listeners) {
			void boundary.runExclusive(() => listener(event)).catch(() => undefined);
		}
		return event;
	}

	private coordinate(active: ActiveRun): RunCoordinate {
		return {
			sessionId: this.sessionId,
			actorId: this.actorId,
			runId: active.runId,
			turn: active.turn.toString(),
		};
	}
}

function freezeJsonValue(value: JsonValue): JsonValue {
	const pending: Array<JsonValue[] | { [key: string]: JsonValue }> = [];
	if (typeof value === "object" && value !== null) pending.push(value);
	while (pending.length > 0) {
		const current = pending.pop();
		if (!current || Object.isFrozen(current)) continue;
		for (const child of Array.isArray(current) ? current : Object.values(current)) {
			if (typeof child === "object" && child !== null) pending.push(child);
		}
		Object.freeze(current);
	}
	return value;
}

function captureModelStream(value: GameModelStream): GameModelStream {
	if (typeof value === "function") return value;
	if (value.kind === "attempts" && typeof value.stream === "function") {
		return Object.freeze({ kind: "attempts", stream: value.stream });
	}
	throw new TypeError("The model stream is invalid.");
}

function captureMaxReportedTokensPerRun(value: number | undefined): number | undefined {
	if (value === undefined) return undefined;
	if (!Number.isSafeInteger(value) || value <= 0) {
		throw new TypeError("The reported-token run limit must be a positive safe integer.");
	}
	return value;
}

function preparationFailureStream(
	model: Parameters<StreamFn>[0],
	aborted: boolean,
	timestamp: number,
): AssistantMessageEventStream {
	const reason = aborted ? "aborted" : "error";
	const message: AssistantMessage = {
		role: "assistant",
		content: [],
		api: model.api,
		provider: model.provider,
		model: model.id,
		usage: EMPTY_USAGE,
		stopReason: reason,
		errorMessage: aborted
			? "Game provider request preparation was aborted."
			: "Game provider request preparation failed.",
		timestamp,
	};
	const { errorMessage: _errorMessage, ...pendingMessage } = message;
	const stream = createAssistantMessageEventStream();
	queueMicrotask(() => {
		stream.push({ type: "start", partial: { ...pendingMessage, stopReason: "pending" } });
		stream.push({ type: "error", reason, error: message });
	});
	return stream;
}

function completedAssistantStream(source: AssistantMessage): AssistantMessageEventStream {
	const reason = source.stopReason;
	if (reason === "pending") throw new TypeError("The deferred provider fetch did not settle.");
	const message = structuredClone(source);
	const stream = createAssistantMessageEventStream();
	queueMicrotask(() => {
		stream.push({ type: "start", partial: { ...message, content: [], stopReason: "pending" } });
		if (reason === "error" || reason === "aborted") {
			stream.push({ type: "error", reason, error: message });
			return;
		}
		stream.push({ type: "done", reason, message });
	});
	return stream;
}

function assistantMessageText(message: Readonly<AssistantMessage>): string | undefined {
	const text = message.content.flatMap((part) => (part.type === "text" ? [part.text] : [])).join("");
	return text.trim().length === 0 ? undefined : text;
}

function captureDeferredHandle(handle: Readonly<DeferredHandle>): DurableTranscriptDeferredHandle {
	return {
		provider: handle.provider,
		modelId: handle.modelId,
		api: handle.api,
		id: handle.id,
		...(handle.expiresAt === undefined ? {} : { expiresAt: handle.expiresAt }),
		...(handle.pollAfterMs === undefined ? {} : { pollAfterMs: handle.pollAfterMs }),
		...(handle.data === undefined ? {} : { data: structuredClone(handle.data) }),
	};
}

function sameDeferredHandle(left: Readonly<DurableTranscriptDeferredHandle>, right: Readonly<DeferredHandle>): boolean {
	return canonicalizeJsonValue(left) === canonicalizeJsonValue(captureDeferredHandle(right));
}

function assertDeferredFetchMessage(
	handle: Readonly<DurableTranscriptDeferredHandle>,
	message: Readonly<AssistantMessage>,
): void {
	if (message.api !== handle.api || message.provider !== handle.provider || message.model !== handle.modelId) {
		throw new TypeError("The deferred provider response does not match its provider origin.");
	}
	if (message.stopReason === "deferred" && (!message.deferred || !sameDeferredHandle(handle, message.deferred))) {
		throw new TypeError("The deferred provider response changed its operation handle.");
	}
}

function sameCoordinate(left: RunCoordinate, right: RunCoordinate): boolean {
	return (
		left.sessionId === right.sessionId &&
		left.actorId === right.actorId &&
		left.runId === right.runId &&
		left.turn === right.turn
	);
}

function isPiMessage(value: unknown): value is Message {
	if (typeof value !== "object" || value === null || !("role" in value)) return false;
	return value.role === "user" || value.role === "assistant" || value.role === "toolResult";
}

function snapshotAgentMessages(messages: readonly AgentMessage[]): AgentMessage[] {
	if (!Array.isArray(messages)) throw new TypeError("The durable transcript message list is invalid.");
	const count = messages.length;
	if (!Number.isSafeInteger(count) || count < 0) throw new TypeError("The durable transcript message list is invalid.");
	const snapshot: AgentMessage[] = [];
	for (let index = 0; index < count; index += 1) snapshot.push(messages[index] as AgentMessage);
	return snapshot;
}

function assertStableToolPolicyExtension(
	previousCandidates: PreparedToolSnapshot,
	previousVisible: VisibleToolSnapshot,
	currentVisible: VisibleToolSnapshot,
): void {
	const before = new Set(previousVisible.visibleToolNames);
	const after = new Set(currentVisible.visibleToolNames);
	for (const descriptor of previousCandidates.descriptors) {
		if (before.has(descriptor.name) !== after.has(descriptor.name)) {
			throw new Error("The Tool policy changed an existing candidate during request-source collection.");
		}
	}
}

function hasProviderResponseContent(message: AssistantMessage): boolean {
	return message.content.some(
		(part) => part.type === "toolCall" || (part.type === "text" ? part.text.length > 0 : part.thinking.length > 0),
	);
}
