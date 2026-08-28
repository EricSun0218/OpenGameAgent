import type {
	GameAgentEvent,
	GameAgentKernelPort,
	GameControlResult,
	GameInput,
	GameKernelRunRequest,
	GameRunCoordinate,
	GameSessionKey,
} from "@opengameagent/protocol";
import { GameAgentRuntime } from "@opengameagent/runtime";
import { describe, expect, it, vi } from "vitest";
import { GameRealtimeAgentBridge } from "./bridge.js";
import type {
	RealtimeAudioFrame,
	RealtimeBehaviorResult,
	RealtimeConversationEvent,
	RealtimeConversationOptions,
	RealtimeHandoffPhase,
	RealtimeTextRole,
	RealtimeTransport,
	RealtimeTransportSession,
} from "./contracts.js";
import { RealtimeConversation } from "./conversation.js";

class EventStream {
	private readonly events: RealtimeConversationEvent[] = [];
	private waiters: Array<() => void> = [];
	private done = false;

	push(event: RealtimeConversationEvent): void {
		this.events.push(event);
		for (const wake of this.waiters.splice(0)) wake();
	}

	close(): void {
		this.done = true;
		for (const wake of this.waiters.splice(0)) wake();
	}

	async *read(signal?: AbortSignal): AsyncIterable<RealtimeConversationEvent> {
		for (;;) {
			if (this.events.length > 0) {
				const event = this.events.shift();
				if (event) yield event;
				continue;
			}
			if (this.done || signal?.aborted) return;
			await new Promise<void>((resolve) => {
				this.waiters.push(resolve);
				signal?.addEventListener("abort", () => resolve(), { once: true });
			});
		}
	}
}

class FakeRealtimeSession implements RealtimeTransportSession {
	readonly features = new Set([
		"audio-input",
		"audio-output",
		"response-cancellation",
		"audio-truncation",
		"handoff",
	] as const);
	readonly stream = new EventStream();
	readonly handoffs: Array<{ id: string; text: string; phase: RealtimeHandoffPhase; completed: boolean }> = [];
	readonly truncations: Array<{ itemId: string; milliseconds: number }> = [];
	cancelled = 0;
	closed = 0;

	events(signal?: AbortSignal): AsyncIterable<RealtimeConversationEvent> {
		return this.stream.read(signal);
	}

	async sendAudio(_frame: RealtimeAudioFrame): Promise<void> {}
	async sendText(_text: string, _role: RealtimeTextRole): Promise<void> {}
	async sendBehaviorResult(_result: RealtimeBehaviorResult): Promise<void> {}

	async sendHandoff(id: string, text: string, phase: RealtimeHandoffPhase, completed: boolean): Promise<void> {
		this.handoffs.push({ id, text, phase, completed });
	}

	async cancelResponse(): Promise<void> {
		this.cancelled += 1;
	}

	async truncateAudio(itemId: string, milliseconds: number): Promise<void> {
		this.truncations.push({ itemId, milliseconds });
	}

	async close(): Promise<void> {
		this.closed += 1;
		this.stream.close();
	}
}

class FakeRealtimeTransport implements RealtimeTransport {
	constructor(readonly session = new FakeRealtimeSession()) {}
	async connect(_options: RealtimeConversationOptions): Promise<RealtimeTransportSession> {
		return this.session;
	}
}

const session: GameSessionKey = {
	worldId: "world",
	saveId: "save",
	timelineId: "timeline",
	generation: 1,
	ownerId: "owner",
	sessionId: "session",
	actorId: "actor",
};

function input(id: string, text: string): GameInput {
	return { id, type: "npc.realtime", session, moment: { tick: 10 }, content: [{ type: "text", text }] };
}

function eventBase(runId: string, turn: number, sequence: number) {
	return {
		runId,
		turn,
		sequence,
		eventId: `${runId}-${sequence}`,
		audience: { visibility: "owner" } as const,
		timestamp: Date.now(),
	};
}

class SteeringKernel implements GameAgentKernelPort {
	steers: Array<{ expected: GameRunCoordinate; input: GameInput }> = [];
	aborts: GameRunCoordinate[] = [];
	private release: (() => void) | undefined;

	releaseRun(): void {
		this.release?.();
	}

	async *run(request: GameKernelRunRequest): AsyncIterable<GameAgentEvent> {
		const publish = async (event: GameAgentEvent): Promise<GameAgentEvent> => {
			await request.beforeEvent?.(event, request.signal);
			return event;
		};
		yield await publish({
			...eventBase(request.runId, 0, 1),
			type: "run.started",
			inputId: request.input.id,
			model: {
				profileId: request.modelProfileId,
				provider: "fake",
				model: "fake",
				api: "fake",
				reasoning: false,
				input: ["text"],
				contextWindow: 8_192,
				maximumOutputTokens: 1_024,
			},
		});
		yield await publish({ ...eventBase(request.runId, 1, 2), type: "turn.started" });
		await new Promise<void>((resolve) => {
			this.release = resolve;
		});
		yield await publish({ ...eventBase(request.runId, 1, 3), type: "message.delta", text: "hello" });
		yield await publish({ ...eventBase(request.runId, 1, 4), type: "message.completed", text: "hello" });
		yield await publish({ ...eventBase(request.runId, 1, 5), type: "run.completed" });
	}

	steer(_session: GameSessionKey, expected: GameRunCoordinate, next: GameInput): GameControlResult {
		this.steers.push({ expected, input: next });
		this.release?.();
		return { accepted: true };
	}

	followUp(): GameControlResult {
		return { accepted: false, reason: "not-active" };
	}

	abort(_session: GameSessionKey, expected: GameRunCoordinate): GameControlResult {
		this.aborts.push(expected);
		this.release?.();
		return { accepted: true };
	}
}

describe("RealtimeConversation", () => {
	it("tracks played audio for barge-in and stops idempotently", async () => {
		const transport = new FakeRealtimeTransport();
		const conversation = new RealtimeConversation(transport);
		await conversation.start({ model: "realtime", voice: "voice" });
		conversation.reportAudioPlayback("audio-1", 120);
		await conversation.bargeIn("audio-1");
		expect(transport.session.cancelled).toBe(1);
		expect(transport.session.truncations).toEqual([{ itemId: "audio-1", milliseconds: 120 }]);
		await Promise.all([conversation.stop(), conversation.stop()]);
		expect(transport.session.closed).toBe(1);
		expect(conversation.state).toBe("closed");
	});

	it("projects transport events in order and handles remote close", async () => {
		const transport = new FakeRealtimeTransport();
		const conversation = new RealtimeConversation(transport);
		const seen: string[] = [];
		conversation.onEvent((event) => {
			seen.push(event.type);
		});
		await conversation.start({ model: "realtime", voice: "voice" });
		transport.session.stream.push({ type: "response.started", timestamp: 1, responseId: "r" });
		transport.session.stream.push({ type: "closed", timestamp: 2 });
		transport.session.stream.close();
		await vi.waitFor(() => expect(conversation.state).toBe("closed"));
		expect(seen).toEqual(["response.started", "closed"]);
		await conversation.stop();
	});
});

describe("GameRealtimeAgentBridge", () => {
	it("steers only the exact run and turn it observed, while keeping provider events separate", async () => {
		const kernel = new SteeringKernel();
		const runtime = new GameAgentRuntime({ kernel, baseSystemPrompt: "base", defaultModelProfileId: "default" });
		const transport = new FakeRealtimeTransport();
		const conversation = new RealtimeConversation(transport);
		await conversation.start({ model: "realtime", voice: "voice" });
		const observed: string[] = [];
		const bridge = new GameRealtimeAgentBridge(
			runtime,
			conversation,
			session,
			async (handoff) => input(handoff.handoffId, handoff.transcript),
			{
				handoffFlushMilliseconds: 10,
				agentEventObserver: (observedInput, event) => {
					expect(observedInput.id).toBe("first");
					observed.push(event.type);
				},
			},
		);
		transport.session.stream.push({
			type: "handoff.requested",
			timestamp: 1,
			handoff: { handoffId: "first", transcript: "first" },
		});
		await vi.waitFor(() => expect(observed).toContain("turn.started"));
		transport.session.stream.push({
			type: "handoff.requested",
			timestamp: 2,
			handoff: { handoffId: "second", transcript: "change target" },
		});
		await vi.waitFor(() => expect(kernel.steers).toHaveLength(1));
		expect(kernel.steers[0]?.expected.turn).toBe(1);
		expect(kernel.steers[0]?.input.id).toBe("second");
		await vi.waitFor(() =>
			expect(transport.session.handoffs.some((value) => value.id === "first" && value.completed)).toBe(true),
		);
		expect(observed).toEqual(["run.started", "turn.started", "message.delta", "message.completed", "run.completed"]);
		await bridge.dispose();
		await conversation.stop();
	});

	it("cancels a pending input factory on disposal without faulting the conversation", async () => {
		const kernel = new SteeringKernel();
		const runtime = new GameAgentRuntime({ kernel, baseSystemPrompt: "base", defaultModelProfileId: "default" });
		const transport = new FakeRealtimeTransport();
		const conversation = new RealtimeConversation(transport);
		await conversation.start({ model: "realtime", voice: "voice" });
		let entered = false;
		let cancelled = false;
		const bridge = new GameRealtimeAgentBridge(runtime, conversation, session, async (_handoff, _session, signal) => {
			entered = true;
			await new Promise<void>((_resolve, reject) => {
				signal.addEventListener(
					"abort",
					() => {
						cancelled = true;
						reject(signal.reason);
					},
					{ once: true },
				);
			});
			return input("never", "never");
		});
		transport.session.stream.push({
			type: "handoff.requested",
			timestamp: 1,
			handoff: { handoffId: "pending", transcript: "wait" },
		});
		await vi.waitFor(() => expect(entered).toBe(true));
		await bridge.dispose();
		await vi.waitFor(() => expect(cancelled).toBe(true));
		expect(conversation.state).toBe("active");
		await conversation.stop();
	});

	it("isolates observer failures and supplies the original authoritative input", async () => {
		const kernel = new SteeringKernel();
		const runtime = new GameAgentRuntime({ kernel, baseSystemPrompt: "base", defaultModelProfileId: "default" });
		const transport = new FakeRealtimeTransport();
		const conversation = new RealtimeConversation(transport);
		await conversation.start({ model: "realtime", voice: "voice" });
		const observed: string[] = [];
		const bridge = new GameRealtimeAgentBridge(
			runtime,
			conversation,
			session,
			async (handoff) => input(handoff.handoffId, handoff.transcript),
			{
				handoffFlushMilliseconds: 10,
				agentEventObserver: (observedInput, event) => {
					expect(observedInput.id).toBe("observer");
					observed.push(event.type);
					if (event.type === "run.started") throw new Error("observer failed");
				},
			},
		);
		transport.session.stream.push({
			type: "handoff.requested",
			timestamp: 1,
			handoff: { handoffId: "observer", transcript: "hello" },
		});
		await vi.waitFor(() => expect(observed).toContain("turn.started"));
		kernel.releaseRun();
		await vi.waitFor(() => expect(observed).toContain("run.completed"));
		await vi.waitFor(() =>
			expect(transport.session.handoffs.some((value) => value.id === "observer" && value.completed)).toBe(true),
		);
		await bridge.dispose();
		await conversation.stop();
	});
});
