import { randomUUID } from "node:crypto";
import { sameGameSession } from "@opengameagent/kernel";
import type {
	GameAgentEvent,
	GameAgentKernelPort,
	GameControlResult,
	GameInput,
	GameRunCoordinate,
	GameSessionKey,
	GameTool,
	GameToolCall,
	GameToolExecutionContext,
	GameToolResult,
	GameUsage,
	JsonValue,
} from "@opengameagent/protocol";
import { ActorScheduler } from "./scheduler.js";
import { preflightGameToolSchema } from "./tool-schema.js";

export interface GameContextSegment {
	name: string;
	priority: number;
	value: JsonValue;
}

export interface GameContextProvider {
	name?: string;
	provide(input: GameInput, signal: AbortSignal): Promise<GameContextSegment | undefined>;
}

export interface GamePostToolContextProvider {
	name?: string;
	provide(
		input: GameInput,
		availableTools: readonly GameTool["definition"][],
		signal: AbortSignal,
	): Promise<GameContextSegment | undefined>;
}

export interface GameToolProvider {
	name?: string;
	provide(input: GameInput, signal: AbortSignal): Promise<readonly GameTool[]>;
}

export interface GameToolVisibilityPolicy {
	isVisible(input: GameInput, tool: GameTool["definition"], signal: AbortSignal): Promise<boolean> | boolean;
}

export interface GameToolExecutionMiddleware {
	execute(
		tool: GameTool["definition"],
		call: GameToolCall,
		context: GameToolExecutionContext,
		next: () => Promise<GameToolResult>,
	): Promise<GameToolResult>;
}

export interface GameModelProfilePolicy {
	name?: string;
	select(input: GameInput, signal: AbortSignal): Promise<string> | string;
}

export type GameRuntimeStage =
	| "run"
	| "queue"
	| "prepare-turn"
	| "context"
	| "tool-provider"
	| "tool-catalog"
	| "post-tool-context"
	| "model-profile"
	| "event-store"
	| "usage-ledger"
	| "tool-execution";

export interface GameRuntimeStageObservation {
	schemaVersion: 1;
	session: GameSessionKey;
	inputId: string;
	runId: string;
	turn: number;
	stage: GameRuntimeStage;
	name?: string;
	startedAt: number;
	durationMilliseconds: number;
	outcome: "ok" | "error" | "cancelled";
	errorCategory?: string;
}

/**
 * Observation-only runtime hook. Implementations must stay bounded; failures are
 * isolated and can never alter Agent execution or game authority.
 */
export interface GameRuntimeObserver {
	observeStage(observation: GameRuntimeStageObservation): void;
	observeEvent(input: GameInput, event: GameAgentEvent): void;
}

export interface GameRuntimeEventStore {
	append(input: GameInput, event: GameAgentEvent, signal: AbortSignal): Promise<void>;
	read?(
		session: GameSessionKey,
		runId: string,
		afterSequence: number,
		maximum: number,
		signal?: AbortSignal,
	): Promise<readonly GameAgentEvent[]>;
}

export type GameUsageCause = "assistant" | "routing" | "compaction" | "media" | "realtime";

export interface GameUsageEntry {
	id: string;
	session: GameSessionKey;
	inputId: string;
	runId: string;
	turn: number;
	cause: GameUsageCause;
	provider?: string;
	model?: string;
	responseId?: string;
	usage: GameUsage;
	timestamp: number;
}

export interface GameUsageTotal {
	records: number;
	input: number;
	output: number;
	cacheRead: number;
	cacheWrite: number;
	reasoning: number;
	totalTokens: number;
	unknownCostRecords: number;
	cost: number | null;
}

export interface GameUsageSummary {
	total: GameUsageTotal;
	byCause: Partial<Record<GameUsageCause, GameUsageTotal>>;
}

export interface GameUsageLedger {
	append(entry: GameUsageEntry, signal?: AbortSignal): Promise<void>;
	summarize(session: GameSessionKey, signal?: AbortSignal): Promise<GameUsageSummary>;
}

export interface GameAgentRuntimeOptions {
	kernel: GameAgentKernelPort;
	baseSystemPrompt: string;
	contextProviders?: readonly GameContextProvider[];
	postToolContextProviders?: readonly GamePostToolContextProvider[];
	toolProviders?: readonly GameToolProvider[];
	toolVisibility?: GameToolVisibilityPolicy;
	toolExecutionMiddleware?: readonly GameToolExecutionMiddleware[];
	modelProfilePolicy?: GameModelProfilePolicy;
	defaultModelProfileId: string;
	eventStore?: GameRuntimeEventStore;
	usageLedger?: GameUsageLedger;
	maximumConcurrentActors?: number;
	maximumQueuedRuns?: number;
	maximumContextCharacters?: number;
	maximumTools?: number;
	maximumTurns?: number;
	observer?: GameRuntimeObserver;
}

export interface GameRunOptions {
	runId?: string;
	signal?: AbortSignal;
	maximumTurns?: number;
	/**
	 * Host-authoritative fence evaluated immediately before every Tool execution
	 * for this run. Returning false prevents the Tool and produces a fixed,
	 * model-visible error result without exposing authorization details.
	 */
	authorizeToolExecution?: (
		tool: GameTool["definition"],
		call: GameToolCall,
		context: GameToolExecutionContext,
	) => Promise<boolean> | boolean;
}

interface ActiveRuntimeRun {
	session: GameSessionKey;
	coordinate: GameRunCoordinate;
}

function sessionKey(session: GameSessionKey): string {
	return JSON.stringify([
		session.worldId,
		session.saveId,
		session.timelineId,
		session.generation,
		session.ownerId,
		session.sessionId,
		session.actorId,
	]);
}

export class GameAgentRuntime {
	private readonly scheduler: ActorScheduler;
	private readonly activeRuns = new Map<string, ActiveRuntimeRun>();
	private readonly maximumContextCharacters: number;
	private readonly maximumTools: number;
	private readonly maximumTurns: number;

	constructor(private readonly options: GameAgentRuntimeOptions) {
		this.scheduler = new ActorScheduler({
			maximumConcurrentActors: options.maximumConcurrentActors ?? 8,
			maximumQueuedRuns: options.maximumQueuedRuns ?? 1024,
		});
		this.maximumContextCharacters = options.maximumContextCharacters ?? 256 * 1024;
		this.maximumTools = options.maximumTools ?? 128;
		this.maximumTurns = options.maximumTurns ?? 32;
		if (!Number.isInteger(this.maximumContextCharacters) || this.maximumContextCharacters < 1024) {
			throw new RangeError("maximumContextCharacters must be an integer of at least 1024.");
		}
		if (!Number.isInteger(this.maximumTools) || this.maximumTools < 0)
			throw new RangeError("maximumTools must be non-negative.");
		if (!Number.isInteger(this.maximumTurns) || this.maximumTurns < 1)
			throw new RangeError("maximumTurns must be positive.");
	}

	async *run(input: GameInput, runOptions: GameRunOptions = {}): AsyncIterable<GameAgentEvent> {
		const signal = runOptions.signal ?? new AbortController().signal;
		const runId = runOptions.runId ?? randomUUID();
		const maximumTurns = runOptions.maximumTurns ?? this.maximumTurns;
		if (!Number.isInteger(maximumTurns) || maximumTurns < 1 || maximumTurns > this.maximumTurns) {
			throw new RangeError(`maximumTurns must be between 1 and ${this.maximumTurns}.`);
		}
		const key = sessionKey(input.session);
		const runStartedAt = Date.now();
		const runMonotonicStartedAt = performance.now();
		let runOutcome: GameRuntimeStageObservation["outcome"] = "ok";
		let runErrorCategory: string | undefined;
		let release: (() => void) | undefined;
		try {
			release = await this.measureStage(
				input,
				runId,
				0,
				"queue",
				undefined,
				signal,
				async () => await this.scheduler.acquire(key, signal),
			);
			const [{ systemPrompt, tools }, modelProfileId] = await Promise.all([
				this.prepareTurn(input, runId, 0, signal, runOptions.authorizeToolExecution),
				this.measureStage(
					input,
					runId,
					0,
					"model-profile",
					this.options.modelProfilePolicy?.name,
					signal,
					async () => await this.selectModelProfile(input, signal),
				),
			]);
			const active: ActiveRuntimeRun = { session: input.session, coordinate: { runId, turn: 0 } };
			this.activeRuns.set(key, active);
			for await (const event of this.options.kernel.run({
				runId,
				input,
				systemPrompt,
				tools,
				modelProfileId,
				maximumTurns,
				prepareNextTurn: async (context, turnSignal) =>
					await this.prepareTurn(input, runId, context.turn, turnSignal, runOptions.authorizeToolExecution),
			})) {
				active.coordinate = { runId: event.runId, turn: event.turn };
				if (event.type === "run.failed") {
					runOutcome = "error";
					runErrorCategory = event.category;
				} else if (event.type === "run.aborted") {
					runOutcome = "cancelled";
					runErrorCategory = "aborted";
				}
				if (this.options.eventStore) {
					await this.measureStage(
						input,
						runId,
						event.turn,
						"event-store",
						event.type,
						signal,
						async () => await this.options.eventStore?.append(input, event, signal),
					);
				}
				if (event.type === "message.completed" && event.usage) {
					const usageEntry: GameUsageEntry = {
						id: event.eventId,
						session: input.session,
						inputId: input.id,
						runId: event.runId,
						turn: event.turn,
						cause: "assistant",
						...(event.provider === undefined ? {} : { provider: event.provider }),
						...(event.model === undefined ? {} : { model: event.model }),
						...(event.responseId === undefined ? {} : { responseId: event.responseId }),
						usage: event.usage,
						timestamp: event.timestamp,
					};
					if (this.options.usageLedger) {
						await this.measureStage(
							input,
							runId,
							event.turn,
							"usage-ledger",
							"assistant",
							signal,
							async () => await this.options.usageLedger?.append(usageEntry, signal),
						);
					}
				}
				this.observeEvent(input, event);
				yield event;
			}
		} catch (error) {
			runOutcome = signal.aborted ? "cancelled" : "error";
			runErrorCategory = safeErrorCategory(error);
			throw error;
		} finally {
			const active = this.activeRuns.get(key);
			if (active?.coordinate.runId === runId) this.activeRuns.delete(key);
			release?.();
			this.observeStage({
				schemaVersion: 1,
				session: structuredClone(input.session),
				inputId: input.id,
				runId,
				turn: active?.coordinate.turn ?? 0,
				stage: "run",
				startedAt: runStartedAt,
				durationMilliseconds: performance.now() - runMonotonicStartedAt,
				outcome: runOutcome,
				...(runErrorCategory === undefined ? {} : { errorCategory: runErrorCategory }),
			});
		}
	}

	private async prepareTurn(
		input: GameInput,
		runId: string,
		turn: number,
		signal: AbortSignal,
		authorizeToolExecution?: GameRunOptions["authorizeToolExecution"],
	): Promise<{ systemPrompt: string; tools: readonly GameTool[] }> {
		return await this.measureStage(input, runId, turn, "prepare-turn", undefined, signal, async () => {
			const [contextSegments, tools] = await Promise.all([
				this.collectContext(input, runId, turn, signal),
				this.collectTools(input, runId, turn, signal, authorizeToolExecution),
			]);
			const postToolSegments = (
				await Promise.all(
					(this.options.postToolContextProviders ?? []).map((provider, index) =>
						this.measureStage(
							input,
							runId,
							turn,
							"post-tool-context",
							provider.name ?? `post-tool-context-${index}`,
							signal,
							async () =>
								await provider.provide(
									input,
									tools.map((tool) => tool.definition),
									signal,
								),
						),
					),
				)
			).filter((segment): segment is GameContextSegment => segment !== undefined);
			return {
				systemPrompt: this.buildSystemPrompt([...contextSegments, ...postToolSegments]),
				tools,
			};
		});
	}

	private async selectModelProfile(input: GameInput, signal: AbortSignal): Promise<string> {
		const profileId =
			(await this.options.modelProfilePolicy?.select(input, signal)) ?? this.options.defaultModelProfileId;
		if (typeof profileId !== "string" || profileId.length < 1 || profileId.length > 128) {
			throw new RangeError("The selected model profile id must contain between 1 and 128 characters.");
		}
		return profileId;
	}

	steer(session: GameSessionKey, expected: GameRunCoordinate, input: GameInput): GameControlResult {
		return this.control(session, expected, () => this.options.kernel.steer(session, expected, input));
	}

	followUp(session: GameSessionKey, expected: GameRunCoordinate, input: GameInput): GameControlResult {
		return this.control(session, expected, () => this.options.kernel.followUp(session, expected, input));
	}

	abort(session: GameSessionKey, expected: GameRunCoordinate): GameControlResult {
		return this.control(session, expected, () => this.options.kernel.abort(session, expected));
	}

	private control(
		session: GameSessionKey,
		expected: GameRunCoordinate,
		action: () => GameControlResult,
	): GameControlResult {
		const active = this.activeRuns.get(sessionKey(session));
		if (!active) return { accepted: false, reason: "not-active" };
		if (!sameGameSession(active.session, session) || active.coordinate.runId !== expected.runId) {
			return { accepted: false, reason: "run-mismatch" };
		}
		if (active.coordinate.turn !== expected.turn) return { accepted: false, reason: "turn-mismatch" };
		return action();
	}

	private async collectContext(
		input: GameInput,
		runId: string,
		turn: number,
		signal: AbortSignal,
	): Promise<GameContextSegment[]> {
		const segments = await Promise.all(
			(this.options.contextProviders ?? []).map((provider, index) =>
				this.measureStage(
					input,
					runId,
					turn,
					"context",
					provider.name ?? `context-${index}`,
					signal,
					async () => await provider.provide(input, signal),
				),
			),
		);

		return segments
			.filter((segment): segment is GameContextSegment => segment !== undefined)
			.sort((left, right) => right.priority - left.priority || left.name.localeCompare(right.name));
	}

	private buildSystemPrompt(segments: readonly GameContextSegment[]): string {
		const ordered = [...segments].sort(
			(left, right) => right.priority - left.priority || left.name.localeCompare(right.name),
		);
		let prompt = this.options.baseSystemPrompt;
		for (const segment of ordered) {
			const serialized = `\n<game-context name=${JSON.stringify(segment.name)}>${JSON.stringify(segment.value)}</game-context>`;
			if (prompt.length + serialized.length > this.maximumContextCharacters) {
				throw new RangeError("Collected game context exceeds the configured character limit.");
			}
			prompt += serialized;
		}
		return prompt;
	}

	private async collectTools(
		input: GameInput,
		runId: string,
		turn: number,
		signal: AbortSignal,
		authorizeToolExecution?: GameRunOptions["authorizeToolExecution"],
	): Promise<GameTool[]> {
		return await this.measureStage(input, runId, turn, "tool-catalog", undefined, signal, async () => {
			const provided = await Promise.all(
				(this.options.toolProviders ?? []).map((provider, index) =>
					this.measureStage(
						input,
						runId,
						turn,
						"tool-provider",
						provider.name ?? `tool-provider-${index}`,
						signal,
						async () => await provider.provide(input, signal),
					),
				),
			);
			const tools = provided.flat();
			if (tools.length > this.maximumTools) throw new RangeError("Collected game tools exceed the configured limit.");
			const names = new Set<string>();
			const visible: GameTool[] = [];
			for (const tool of tools) {
				if (names.has(tool.definition.name)) throw new Error(`Duplicate game tool '${tool.definition.name}'.`);
				names.add(tool.definition.name);
				preflightGameToolSchema(tool.definition);
				if ((await this.options.toolVisibility?.isVisible(input, tool.definition, signal)) === false) continue;
				visible.push(this.wrapTool(tool, authorizeToolExecution));
			}
			return visible;
		});
	}

	private wrapTool(tool: GameTool, authorizeToolExecution?: GameRunOptions["authorizeToolExecution"]): GameTool {
		const middleware = this.options.toolExecutionMiddleware ?? [];
		const executable: GameTool = {
			definition: tool.definition,
			execute: async (call, context) => {
				if (authorizeToolExecution && !(await authorizeToolExecution(tool.definition, call, context))) {
					return {
						isError: true,
						content: [{ type: "json", value: { error: "run_authority_expired" } }],
					};
				}
				if (middleware.length === 0) return tool.execute(call, context);
				let index = middleware.length;
				const next = (): Promise<GameToolResult> => {
					index -= 1;
					if (index < 0) return tool.execute(call, context);
					const current = middleware[index];
					if (!current) throw new Error("Tool execution middleware chain is corrupt.");
					return current.execute(tool.definition, call, context, next);
				};
				return next();
			},
		};
		return {
			definition: executable.definition,
			execute: async (call, context) =>
				await this.measureStage(
					context.input,
					context.runId,
					context.turn,
					"tool-execution",
					tool.definition.name,
					context.signal,
					async () => await executable.execute(call, context),
				),
		};
	}

	private async measureStage<T>(
		input: GameInput,
		runId: string,
		turn: number,
		stage: GameRuntimeStage,
		name: string | undefined,
		signal: AbortSignal,
		operation: () => Promise<T>,
	): Promise<T> {
		const startedAt = Date.now();
		const monotonicStartedAt = performance.now();
		try {
			const result = await operation();
			this.observeStage({
				schemaVersion: 1,
				session: structuredClone(input.session),
				inputId: input.id,
				runId,
				turn,
				stage,
				...(name === undefined ? {} : { name }),
				startedAt,
				durationMilliseconds: performance.now() - monotonicStartedAt,
				outcome: "ok",
			});
			return result;
		} catch (error) {
			this.observeStage({
				schemaVersion: 1,
				session: structuredClone(input.session),
				inputId: input.id,
				runId,
				turn,
				stage,
				...(name === undefined ? {} : { name }),
				startedAt,
				durationMilliseconds: performance.now() - monotonicStartedAt,
				outcome: signal.aborted ? "cancelled" : "error",
				errorCategory: safeErrorCategory(error),
			});
			throw error;
		}
	}

	private observeStage(observation: GameRuntimeStageObservation): void {
		try {
			this.options.observer?.observeStage(observation);
		} catch {
			// Observation is intentionally isolated from runtime behavior.
		}
	}

	private observeEvent(input: GameInput, event: GameAgentEvent): void {
		try {
			this.options.observer?.observeEvent(input, event);
		} catch {
			// Observation is intentionally isolated from runtime behavior.
		}
	}
}

function safeErrorCategory(error: unknown): string {
	if (error instanceof DOMException && error.name === "AbortError") return "aborted";
	if (error instanceof Error && /^[A-Za-z][A-Za-z0-9_.-]{0,63}$/u.test(error.name)) return error.name;
	return "unknown";
}
