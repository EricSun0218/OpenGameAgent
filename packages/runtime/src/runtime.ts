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

export interface GameToolProvider {
	provide(input: GameInput, signal: AbortSignal): Promise<readonly GameTool[]>;
}

export interface GameToolVisibilityPolicy {
	isVisible(input: GameInput, tool: GameTool["definition"], signal: AbortSignal): Promise<boolean> | boolean;
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

export interface GameAgentRuntimeOptions {
	kernel: GameAgentKernelPort;
	baseSystemPrompt: string;
	contextProviders?: readonly GameContextProvider[];
	toolProviders?: readonly GameToolProvider[];
	toolVisibility?: GameToolVisibilityPolicy;
	eventStore?: GameRuntimeEventStore;
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
			const [systemPrompt, tools] = await Promise.all([
				this.buildSystemPrompt(input, signal),
				this.collectTools(input, signal),
			]);
			const active: ActiveRuntimeRun = { session: input.session, coordinate: { runId, turn: 0 } };
			this.activeRuns.set(key, active);
			for await (const event of this.options.kernel.run({
				runId,
				input,
				systemPrompt,
				tools,
				maximumTurns: this.maximumTurns,
			})) {
				active.coordinate = { runId: event.runId, turn: event.turn };
				await this.options.eventStore?.append(input, event, signal);
				yield event;
			}
		} finally {
			const active = this.activeRuns.get(key);
			if (active?.coordinate.runId === runId) this.activeRuns.delete(key);
			release();
		}
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

	private async buildSystemPrompt(input: GameInput, signal: AbortSignal): Promise<string> {
		const segments = (
			await Promise.all((this.options.contextProviders ?? []).map((provider) => provider.provide(input, signal)))
		)
			.filter((segment): segment is GameContextSegment => segment !== undefined)
			.sort((left, right) => right.priority - left.priority || left.name.localeCompare(right.name));
		let prompt = this.options.baseSystemPrompt;
		for (const segment of segments) {
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
			visible.push(tool);
		}
		return visible;
	}
}
