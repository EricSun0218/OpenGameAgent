import { randomUUID } from "node:crypto";
import type {
	GameAgentEvent,
	GameControlResult,
	GameInput,
	GameRunCoordinate,
	GameUsage,
	JsonValue,
} from "@opengameagent/protocol";
import type { GameAgentRuntime } from "@opengameagent/runtime";
import type { GameDelegationOutcome, GameDelegationRequest } from "./delegation.js";

export interface GameDelegationExecutionAuthority {
	isAuthoritative(signal?: AbortSignal): Promise<boolean>;
}

export interface GameDelegationHandle extends AsyncDisposable {
	completion: Promise<GameDelegationOutcome>;
	steer(message: JsonValue, signal?: AbortSignal): Promise<GameControlResult>;
	abort(): GameControlResult;
}

export interface GameDelegationExecutor {
	start(
		request: GameDelegationRequest,
		authority: GameDelegationExecutionAuthority,
		signal: AbortSignal,
	): GameDelegationHandle;
}

export interface RuntimeGameDelegationExecutorOptions {
	runtime?: GameAgentRuntime;
	getRuntime?: () => GameAgentRuntime;
	createInput(request: GameDelegationRequest, signal: AbortSignal): Promise<GameInput> | GameInput;
	createSteerInput?(
		request: GameDelegationRequest,
		childInput: GameInput,
		message: JsonValue,
		signal: AbortSignal,
	): Promise<GameInput> | GameInput;
}

function addUsage(left: GameUsage | undefined, right: GameUsage | undefined): GameUsage | undefined {
	if (!right) return left;
	if (!left) return structuredClone(right);
	const cost =
		left.cost === undefined || right.cost === undefined
			? undefined
			: {
					input: left.cost.input + right.cost.input,
					output: left.cost.output + right.cost.output,
					cacheRead: left.cost.cacheRead + right.cost.cacheRead,
					cacheWrite: left.cost.cacheWrite + right.cost.cacheWrite,
					total: left.cost.total + right.cost.total,
				};
	return {
		input: left.input + right.input,
		output: left.output + right.output,
		cacheRead: left.cacheRead + right.cacheRead,
		cacheWrite: left.cacheWrite + right.cacheWrite,
		...(left.reasoning === undefined && right.reasoning === undefined
			? {}
			: { reasoning: (left.reasoning ?? 0) + (right.reasoning ?? 0) }),
		totalTokens: left.totalTokens + right.totalTokens,
		...(cost === undefined ? {} : { cost }),
	};
}

class RuntimeGameDelegationHandle implements GameDelegationHandle {
	readonly completion: Promise<GameDelegationOutcome>;
	private readonly cancellation: AbortController;
	private coordinate?: GameRunCoordinate;
	private childInput?: GameInput;
	private closed = false;

	constructor(
		private readonly options: RuntimeGameDelegationExecutorOptions,
		private readonly request: GameDelegationRequest,
		private readonly authority: GameDelegationExecutionAuthority,
		signal: AbortSignal,
	) {
		this.cancellation = new AbortController();
		const combined = AbortSignal.any([signal, this.cancellation.signal]);
		this.completion = this.run(combined);
	}

	async steer(message: JsonValue, signal?: AbortSignal): Promise<GameControlResult> {
		signal?.throwIfAborted();
		if (this.closed || !this.coordinate || !this.childInput) return { accepted: false, reason: "not-active" };
		const controlInput = this.options.createSteerInput
			? await this.options.createSteerInput(
					this.request,
					this.childInput,
					message,
					signal ?? new AbortController().signal,
				)
			: {
					id: randomUUID(),
					type: "agent.delegation.steer",
					session: structuredClone(this.childInput.session),
					moment: structuredClone(this.request.parentMoment),
					content: [{ type: "json" as const, value: structuredClone(message) }],
				};
		return resolveRuntime(this.options).steer(this.childInput.session, this.coordinate, controlInput);
	}

	abort(): GameControlResult {
		if (this.closed || !this.coordinate || !this.childInput) {
			this.cancellation.abort();
			return { accepted: false, reason: "not-active" };
		}
		const result = resolveRuntime(this.options).abort(this.childInput.session, this.coordinate);
		this.cancellation.abort();
		return result;
	}

	async [Symbol.asyncDispose](): Promise<void> {
		this.abort();
		try {
			await this.completion;
		} catch {
			// Completion is converted to a typed outcome by run().
		}
	}

	private async run(signal: AbortSignal): Promise<GameDelegationOutcome> {
		let text = "";
		let usage: GameUsage | undefined;
		let terminal: GameAgentEvent | undefined;
		try {
			const childInput = await this.options.createInput(this.request, signal);
			this.childInput = structuredClone(childInput);
			const runId = randomUUID();
			for await (const event of resolveRuntime(this.options).run(childInput, {
				runId,
				signal,
				maximumTurns: this.request.maximumTurns,
				authorizeToolExecution: async (_tool, _call, context) => await this.authority.isAuthoritative(context.signal),
			})) {
				this.coordinate = { runId: event.runId, turn: event.turn };
				if (event.type === "message.completed") {
					text = event.text;
					usage = addUsage(usage, event.usage);
				}
				if (event.type === "run.completed" || event.type === "run.failed" || event.type === "run.aborted") {
					terminal = event;
				}
			}
			if (!terminal) return { status: "failed", error: "Delegated run ended without a terminal event." };
			if (terminal.type === "run.aborted") return { status: "cancelled", error: "Delegated run was cancelled." };
			if (terminal.type === "run.failed") {
				return { status: "failed", error: `${terminal.category}: ${terminal.message}`.slice(0, 4_096) };
			}
			return {
				status: "completed",
				result: {
					text,
					...(usage === undefined ? {} : { usage: usage as unknown as JsonValue }),
				},
			};
		} catch (error) {
			if (signal.aborted) return { status: "cancelled", error: "Delegated run was cancelled." };
			return {
				status: "failed",
				error: (error instanceof Error ? `${error.name}: ${error.message}` : "Delegated run failed.").slice(0, 4_096),
			};
		} finally {
			this.closed = true;
		}
	}
}

export class RuntimeGameDelegationExecutor implements GameDelegationExecutor {
	constructor(private readonly options: RuntimeGameDelegationExecutorOptions) {
		if ((!options.runtime && !options.getRuntime) || (options.runtime && options.getRuntime) || !options.createInput)
			throw new TypeError("Runtime delegation executor options are incomplete.");
	}

	start(
		request: GameDelegationRequest,
		authority: GameDelegationExecutionAuthority,
		signal: AbortSignal,
	): GameDelegationHandle {
		return new RuntimeGameDelegationHandle(this.options, structuredClone(request), authority, signal);
	}
}

function resolveRuntime(options: RuntimeGameDelegationExecutorOptions): GameAgentRuntime {
	const runtime = options.runtime ?? options.getRuntime?.();
	if (!runtime) throw new Error("The delegated-agent runtime is not ready.");
	return runtime;
}
