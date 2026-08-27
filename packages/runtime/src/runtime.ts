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
	provide(input: GameInput, signal: AbortSignal): Promise<GameContextSegment | undefined>;
}

export interface GamePostToolContextProvider {
	provide(
		input: GameInput,
		availableTools: readonly GameTool["definition"][],
		signal: AbortSignal,
	): Promise<GameContextSegment | undefined>;
}

export interface GameToolProvider {
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
	select(input: GameInput, signal: AbortSignal): Promise<string> | string;
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
}

export interface GameRunOptions {
	runId?: string;
	signal?: AbortSignal;
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
		const key = sessionKey(input.session);
		const release = await this.scheduler.acquire(key, signal);
		try {
			const [{ systemPrompt, tools }, modelProfileId] = await Promise.all([
				this.prepareTurn(input, signal),
				this.selectModelProfile(input, signal),
			]);
			const active: ActiveRuntimeRun = { session: input.session, coordinate: { runId, turn: 0 } };
			this.activeRuns.set(key, active);
			for await (const event of this.options.kernel.run({
				runId,
				input,
				systemPrompt,
				tools,
				modelProfileId,
				maximumTurns: this.maximumTurns,
				prepareNextTurn: async (_context, turnSignal) => await this.prepareTurn(input, turnSignal),
			})) {
				active.coordinate = { runId: event.runId, turn: event.turn };
				await this.options.eventStore?.append(input, event, signal);
				if (event.type === "message.completed" && event.usage) {
					await this.options.usageLedger?.append(
						{
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
						},
						signal,
					);
				}
				yield event;
			}
		} finally {
			const active = this.activeRuns.get(key);
			if (active?.coordinate.runId === runId) this.activeRuns.delete(key);
			release();
		}
	}

	private async prepareTurn(
		input: GameInput,
		signal: AbortSignal,
	): Promise<{ systemPrompt: string; tools: readonly GameTool[] }> {
		const [contextSegments, tools] = await Promise.all([
			this.collectContext(input, signal),
			this.collectTools(input, signal),
		]);
		const postToolSegments = (
			await Promise.all(
				(this.options.postToolContextProviders ?? []).map((provider) =>
					provider.provide(
						input,
						tools.map((tool) => tool.definition),
						signal,
					),
				),
			)
		).filter((segment): segment is GameContextSegment => segment !== undefined);
		return {
			systemPrompt: this.buildSystemPrompt([...contextSegments, ...postToolSegments]),
			tools,
		};
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

	private async collectContext(input: GameInput, signal: AbortSignal): Promise<GameContextSegment[]> {
		return (await Promise.all((this.options.contextProviders ?? []).map((provider) => provider.provide(input, signal))))
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

	private async collectTools(input: GameInput, signal: AbortSignal): Promise<GameTool[]> {
		const provided = await Promise.all(
			(this.options.toolProviders ?? []).map((provider) => provider.provide(input, signal)),
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
			visible.push(this.wrapTool(tool));
		}
		return visible;
	}

	private wrapTool(tool: GameTool): GameTool {
		const middleware = this.options.toolExecutionMiddleware ?? [];
		if (middleware.length === 0) return tool;
		return {
			definition: tool.definition,
			execute: (call, context) => {
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
	}
}
