import type { GameAgentEvent, GameInput, GameRunCoordinate, GameSessionKey } from "@opengameagent/protocol";
import type { GameAgentRuntime } from "@opengameagent/runtime";
import type { RealtimeGameInputFactory, RealtimeHandoffRequest } from "./contracts.js";
import type { RealtimeConversation } from "./conversation.js";

export interface GameRealtimeAgentBridgeOptions {
	maximumQueuedHandoffs?: number;
	maximumHandoffCharacters?: number;
	handoffFlushMilliseconds?: number;
	steerActiveRun?: boolean;
	agentEventObserver?: (input: GameInput, event: GameAgentEvent, signal: AbortSignal) => void | Promise<void>;
}

interface CheckedBridgeOptions {
	maximumQueuedHandoffs: number;
	maximumHandoffCharacters: number;
	handoffFlushMilliseconds: number;
	steerActiveRun: boolean;
	agentEventObserver?: (input: GameInput, event: GameAgentEvent, signal: AbortSignal) => void | Promise<void>;
}

interface ActiveRun {
	coordinate?: GameRunCoordinate;
	controller: AbortController;
}

interface PendingHandoff {
	handoff: RealtimeHandoffRequest;
	input: Awaited<ReturnType<RealtimeGameInputFactory>>;
}

function checkedOptions(options: GameRealtimeAgentBridgeOptions): CheckedBridgeOptions {
	const maximumQueuedHandoffs = options.maximumQueuedHandoffs ?? 32;
	const maximumHandoffCharacters = options.maximumHandoffCharacters ?? 1_000_000;
	const handoffFlushMilliseconds = options.handoffFlushMilliseconds ?? 200;
	if (!Number.isInteger(maximumQueuedHandoffs) || maximumQueuedHandoffs < 1 || maximumQueuedHandoffs > 1_024)
		throw new RangeError("maximumQueuedHandoffs is invalid.");
	if (
		!Number.isInteger(maximumHandoffCharacters) ||
		maximumHandoffCharacters < 1 ||
		maximumHandoffCharacters > 4_000_000
	)
		throw new RangeError("maximumHandoffCharacters is invalid.");
	if (!Number.isInteger(handoffFlushMilliseconds) || handoffFlushMilliseconds < 10 || handoffFlushMilliseconds > 5_000)
		throw new RangeError("handoffFlushMilliseconds is invalid.");
	return {
		maximumQueuedHandoffs,
		maximumHandoffCharacters,
		handoffFlushMilliseconds,
		steerActiveRun: options.steerActiveRun ?? true,
		...(options.agentEventObserver === undefined ? {} : { agentEventObserver: options.agentEventObserver }),
	};
}

function sameSession(left: GameSessionKey, right: GameSessionKey): boolean {
	return (
		left.worldId === right.worldId &&
		left.saveId === right.saveId &&
		left.timelineId === right.timelineId &&
		left.generation === right.generation &&
		left.ownerId === right.ownerId &&
		left.sessionId === right.sessionId &&
		left.actorId === right.actorId
	);
}

export class GameRealtimeAgentBridge {
	private readonly options: CheckedBridgeOptions;
	private readonly queue: PendingHandoff[] = [];
	private readonly lifetime = new AbortController();
	private readonly unsubscribe: () => void;
	private active: ActiveRun | undefined;
	private pumping = false;
	private disposed = false;

	constructor(
		private readonly runtime: GameAgentRuntime,
		private readonly conversation: RealtimeConversation,
		private readonly session: GameSessionKey,
		private readonly inputFactory: RealtimeGameInputFactory,
		options: GameRealtimeAgentBridgeOptions = {},
	) {
		this.options = checkedOptions(options);
		this.unsubscribe = conversation.onEvent((event, signal) => {
			if (event.type === "handoff.requested" && event.handoff.clientManaged !== true)
				return this.accept(event.handoff, signal);
		});
	}

	get hasActiveAgentRun(): boolean {
		return this.active !== undefined;
	}

	async dispose(): Promise<void> {
		if (this.disposed) return;
		this.disposed = true;
		this.unsubscribe();
		this.lifetime.abort();
		const active = this.active;
		if (active?.coordinate) this.runtime.abort(this.session, active.coordinate);
		active?.controller.abort();
	}

	private async accept(handoff: RealtimeHandoffRequest, signal: AbortSignal): Promise<void> {
		if (this.disposed) return;
		const handoffSignal = AbortSignal.any([signal, this.lifetime.signal]);
		if (!handoff.transcript || handoff.transcript.length > this.options.maximumHandoffCharacters) {
			if (!handoff.isTranscriptTail)
				await this.conversation.sendHandoff(handoff.handoffId, "Handoff rejected.", "final", true, handoffSignal);
			return;
		}
		let input: Awaited<ReturnType<RealtimeGameInputFactory>>;
		try {
			input = await this.inputFactory(handoff, this.session, handoffSignal);
		} catch (error) {
			if (handoffSignal.aborted) return;
			throw error;
		}
		if (this.disposed || handoffSignal.aborted) return;
		if (!sameSession(input.session, this.session))
			throw new Error("Realtime input factory changed the authoritative session.");
		const active = this.active;
		const coordinate = active?.coordinate;
		if (this.options.steerActiveRun && active && coordinate) {
			const result = this.runtime.steer(this.session, coordinate, input);
			if (result.accepted) {
				if (!handoff.isTranscriptTail)
					await this.conversation.sendHandoff(handoff.handoffId, "", "commentary", false, handoffSignal);
				return;
			}
		}
		if (this.queue.length >= this.options.maximumQueuedHandoffs) {
			if (!handoff.isTranscriptTail)
				await this.conversation.sendHandoff(handoff.handoffId, "Handoff queue is full.", "final", true, handoffSignal);
			return;
		}
		this.queue.push({ handoff, input });
		if (!this.pumping) void this.pump();
	}

	private async pump(): Promise<void> {
		this.pumping = true;
		try {
			while (!this.disposed && this.queue.length > 0) {
				const pending = this.queue.shift();
				if (pending) await this.run(pending);
			}
		} finally {
			this.pumping = false;
		}
	}

	private async run(pending: PendingHandoff): Promise<void> {
		const { handoff, input } = pending;
		const controller = new AbortController();
		const signal = AbortSignal.any([controller.signal, this.lifetime.signal]);
		const active: ActiveRun = { controller };
		this.active = active;
		try {
			let buffer = "";
			let lastFlush = Date.now();
			const observer = this.options.agentEventObserver;
			const agentEventObserver =
				observer === undefined
					? undefined
					: async (observedInput: GameInput, event: GameAgentEvent, eventSignal: AbortSignal) =>
							await observer(observedInput, event, eventSignal);
			for await (const event of this.runtime.run(input, {
				signal,
				...(agentEventObserver === undefined ? {} : { agentEventObserver }),
			})) {
				active.coordinate = { runId: event.runId, turn: event.turn };
				if (handoff.isTranscriptTail) continue;
				if (event.type === "message.delta") {
					buffer += event.text;
					if (Date.now() - lastFlush >= this.options.handoffFlushMilliseconds) {
						await this.conversation.sendHandoff(handoff.handoffId, buffer, "commentary", false, signal);
						buffer = "";
						lastFlush = Date.now();
					}
				} else if (event.type === "message.completed") {
					await this.conversation.sendHandoff(handoff.handoffId, buffer || event.text, "final", true, signal);
					buffer = "";
				} else if (event.type === "run.failed") {
					await this.conversation.sendHandoff(handoff.handoffId, "Agent run failed.", "final", true, signal);
				}
			}
		} catch {
			if (!signal.aborted && !handoff.isTranscriptTail) {
				await this.conversation.sendHandoff(handoff.handoffId, "Agent handoff failed.", "final", true);
			}
		} finally {
			if (this.active === active) this.active = undefined;
		}
	}
}
