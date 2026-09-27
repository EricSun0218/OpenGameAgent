import type { RealtimeAudioChunk, RealtimeVoiceCoordinate, RealtimeVoiceTurnHandle } from "@opengameagent/realtime";
import { describe, expect, it, vi } from "vitest";
import {
	GameContinuousLocalSpeechSession,
	LocalSpeechCapacityError,
	LocalSpeechProtocolError,
	type VoiceActivityUtteranceSession,
	PCM_S16LE_MEDIA_TYPE,
} from "../src/index.js";

const INPUT_FORMAT = {
	mediaType: PCM_S16LE_MEDIA_TYPE,
	sampleRateHz: 16_000,
	channelCount: 1,
} as const;
const OUTPUT_FORMAT = { mediaType: "audio/pcm", sampleRateHz: 24_000, channelCount: 1 } as const;

describe("GameContinuousLocalSpeechSession", () => {
	it("validates the full start request before opening the endpoint adapter", () => {
		const openSession = vi.fn(async () => ({ captureNext: async () => undefined, close: () => undefined }));
		const makeSession = () =>
			new GameContinuousLocalSpeechSession({
				voice: { start: vi.fn(), interrupt: vi.fn() },
				endpointing: { openSession },
				maxPendingTurns: 1,
			});

		expect(() => makeSession().start({ ...baseRequest(), inputFormat: { ...INPUT_FORMAT, channelCount: 0 } })).toThrow(
			LocalSpeechProtocolError,
		);
		expect(() =>
			makeSession().start({ ...baseRequest(), outputFormat: { ...OUTPUT_FORMAT, sampleRateHz: 0 } }),
		).toThrow(LocalSpeechProtocolError);
		expect(() => makeSession().start({ ...baseRequest(), voiceProfileId: "" })).toThrow(LocalSpeechProtocolError);
		expect(() => makeSession().start({ ...baseRequest(), audio: {} as never })).toThrow(LocalSpeechProtocolError);
		expect(openSession).not.toHaveBeenCalled();
	});

	it("turns one owned microphone stream into consecutive exact-coordinate turns", async () => {
		const trace: string[] = [];
		let sessionCloses = 0;
		let nextUtterance = 0;
		const startedTurns: RealtimeVoiceTurnHandle[] = [];
		const endpointSession: VoiceActivityUtteranceSession = {
			async captureNext(request = {}) {
				nextUtterance += 1;
				if (nextUtterance > 2) return undefined;
				await request.onSpeechStart?.();
				return utterance(nextUtterance);
			},
			close() {
				sessionCloses += 1;
			},
		};
		const start = vi.fn((request: { coordinate: RealtimeVoiceCoordinate }) => {
			trace.push(`start:${request.coordinate.utteranceId}`);
			const startedTurn = turn(request.coordinate.utteranceId);
			startedTurns.push(startedTurn);
			return startedTurn;
		});
		const interrupt = vi.fn(async (coordinate: RealtimeVoiceCoordinate) => {
			trace.push(`interrupt:${coordinate.utteranceId}`);
			return { accepted: true as const, coordinate };
		});
		const session = new GameContinuousLocalSpeechSession({
			voice: { start, interrupt },
			endpointing: { openSession: async () => endpointSession },
			maxPendingTurns: 3,
		});
		const handle = session.start({
			inputFormat: INPUT_FORMAT,
			outputFormat: OUTPUT_FORMAT,
			voiceProfileId: "guard",
			audio: audio(),
			createCoordinate: (utteranceIndex) => ({
				sessionId: "session",
				actorId: "actor",
				voiceSessionId: "voice",
				epoch: String(utteranceIndex),
				utteranceId: `u${utteranceIndex}`,
			}),
		});

		const started = await collect(handle.turns);

		expect(started.map((value) => value.coordinate.utteranceId)).toEqual(["u1", "u2"]);
		expect(started.map((value) => value.turn)).toEqual(startedTurns);
		expect(started[0]?.interrupt).toBeUndefined();
		expect(started[1]?.interrupt).toEqual({ accepted: true, coordinate: started[0]?.coordinate });
		await expect(handle.result).resolves.toEqual({ status: "source-ended" });
		expect(trace).toEqual(["start:u1", "interrupt:u1", "start:u2", "interrupt:u2"]);
		expect({ sessionCloses, starts: start.mock.calls.length, interrupts: interrupt.mock.calls.length }).toEqual({
			sessionCloses: 1,
			starts: 2,
			interrupts: 2,
		});
	});

	it("fails closed before reading another utterance when the host changes voice channel coordinates", async () => {
		let captures = 0;
		let sessionCloses = 0;
		const interrupt = vi.fn(async (coordinate: RealtimeVoiceCoordinate) => ({ accepted: true as const, coordinate }));
		const session = new GameContinuousLocalSpeechSession({
			voice: {
				start: (request) => turn(request.coordinate.utteranceId),
				interrupt,
			},
			endpointing: {
				openSession: async () => ({
					async captureNext(request = {}) {
						captures += 1;
						await request.onSpeechStart?.();
						return utterance(captures);
					},
					close: () => {
						sessionCloses += 1;
					},
				}),
			},
			maxPendingTurns: 2,
		});
		const handle = session.start({
			...baseRequest(),
			createCoordinate: (index) => ({
				sessionId: "session",
				actorId: index === 1 ? "actor" : "different-actor",
				voiceSessionId: "voice",
				epoch: String(index),
				utteranceId: `u${index}`,
			}),
		});

		await expect(handle.result).rejects.toBeInstanceOf(LocalSpeechProtocolError);
		const accepted = await collect(handle.turns);
		expect(accepted.map((value) => value.coordinate.utteranceId)).toEqual(["u1"]);
		expect({ captures, sessionCloses }).toEqual({ captures: 1, sessionCloses: 1 });
		expect(interrupt).toHaveBeenCalledTimes(1);
		expect(interrupt.mock.calls[0]?.[0]).toMatchObject({ actorId: "actor", utteranceId: "u1" });
	});

	it("stops a pending next utterance and interrupts only the exact active turn", async () => {
		const reason = new Error("player stopped microphone");
		let openSignal: AbortSignal | undefined;
		let captures = 0;
		let sessionCloses = 0;
		const start = vi.fn((request: { coordinate: RealtimeVoiceCoordinate }) => turn(request.coordinate.utteranceId));
		const interrupt = vi.fn(async (coordinate: RealtimeVoiceCoordinate, actualReason?: unknown) => ({
			accepted: true as const,
			coordinate,
			actualReason,
		}));
		const session = new GameContinuousLocalSpeechSession({
			voice: { start, interrupt },
			endpointing: {
				openSession: async (request) => {
					openSignal = request.signal;
					return {
						async captureNext(captureRequest = {}) {
							captures += 1;
							if (captures === 1) {
								await captureRequest.onSpeechStart?.();
								return utterance(1);
							}
							return new Promise((_, reject) => {
								openSignal?.addEventListener("abort", () => reject(openSignal?.reason), { once: true });
							});
						},
						close() {
							sessionCloses += 1;
						},
					};
				},
			},
			maxPendingTurns: 1,
		});
		const handle = session.start(baseRequest());
		const iterator = handle.turns[Symbol.asyncIterator]();
		const first = await iterator.next();

		handle.stop(reason);

		expect(first.value?.coordinate.utteranceId).toBe("u1");
		await expect(handle.result).resolves.toEqual({ status: "stopped" });
		expect(await iterator.next()).toEqual({ done: true, value: undefined });
		expect(sessionCloses).toBe(1);
		expect(interrupt).toHaveBeenCalledTimes(1);
		expect(interrupt.mock.calls[0]).toEqual([expect.objectContaining({ utteranceId: "u1" }), reason]);
	});

	it("preserves caller cancellation while closing the source session and active turn once", async () => {
		const controller = new AbortController();
		const reason = new Error("host microphone lease revoked");
		let sessionCloses = 0;
		let openSignal: AbortSignal | undefined;
		const interrupt = vi.fn(async (coordinate: RealtimeVoiceCoordinate) => ({ accepted: true as const, coordinate }));
		const session = new GameContinuousLocalSpeechSession({
			voice: {
				start: (request) => turn(request.coordinate.utteranceId),
				interrupt,
			},
			endpointing: {
				openSession: async (request) => {
					openSignal = request.signal;
					let capture = 0;
					return {
						async captureNext(captureRequest = {}) {
							capture += 1;
							if (capture === 1) {
								await captureRequest.onSpeechStart?.();
								return utterance(1);
							}
							return new Promise((_, reject) => {
								openSignal?.addEventListener("abort", () => reject(openSignal?.reason), { once: true });
							});
						},
						close: () => {
							sessionCloses += 1;
						},
					};
				},
			},
			maxPendingTurns: 1,
		});
		const handle = session.start({ ...baseRequest(), signal: controller.signal });
		const iterator = handle.turns[Symbol.asyncIterator]();
		await iterator.next();

		controller.abort(reason);

		await expect(handle.result).rejects.toBe(reason);
		expect(sessionCloses).toBe(1);
		expect(interrupt).toHaveBeenCalledTimes(1);
		expect(interrupt.mock.calls[0]).toEqual([expect.objectContaining({ utteranceId: "u1" }), reason]);
	});

	it("preserves detector failure while closing the source session and exact active turn once", async () => {
		const failure = new Error("detector failed");
		let captures = 0;
		let sessionCloses = 0;
		const interrupt = vi.fn(async (coordinate: RealtimeVoiceCoordinate, reason?: unknown) => ({
			accepted: true as const,
			coordinate,
			reason,
		}));
		const session = new GameContinuousLocalSpeechSession({
			voice: {
				start: (request) => turn(request.coordinate.utteranceId),
				interrupt,
			},
			endpointing: {
				openSession: async () => ({
					async captureNext(request = {}) {
						captures += 1;
						if (captures > 1) throw failure;
						await request.onSpeechStart?.();
						return utterance(1);
					},
					close: () => {
						sessionCloses += 1;
					},
				}),
			},
			maxPendingTurns: 1,
		});
		const handle = session.start(baseRequest());

		await expect(handle.result).rejects.toBe(failure);
		expect(await collect(handle.turns)).toHaveLength(1);
		expect({ captures, sessionCloses }).toEqual({ captures: 2, sessionCloses: 1 });
		expect(interrupt).toHaveBeenCalledTimes(1);
		expect(interrupt.mock.calls[0]).toEqual([expect.objectContaining({ utteranceId: "u1" }), failure]);
	});

	it("fails closed when started turns outrun the explicit handoff capacity", async () => {
		let capture = 0;
		let sessionCloses = 0;
		const start = vi.fn((request: { coordinate: RealtimeVoiceCoordinate }) => turn(request.coordinate.utteranceId));
		const interrupt = vi.fn(async (coordinate: RealtimeVoiceCoordinate) => ({ accepted: true as const, coordinate }));
		const session = new GameContinuousLocalSpeechSession({
			voice: { start, interrupt },
			endpointing: {
				openSession: async () => ({
					async captureNext(request = {}) {
						capture += 1;
						await request.onSpeechStart?.();
						return utterance(capture);
					},
					close: () => {
						sessionCloses += 1;
					},
				}),
			},
			maxPendingTurns: 1,
		});
		const handle = session.start(baseRequest());

		await expect(handle.result).rejects.toBeInstanceOf(LocalSpeechCapacityError);
		const acceptedPrefix = await collect(handle.turns);
		expect(acceptedPrefix.map((value) => value.coordinate.utteranceId)).toEqual(["u1"]);
		expect({
			captures: capture,
			sessionCloses,
			starts: start.mock.calls.length,
			interrupts: interrupt.mock.calls.length,
		}).toEqual({
			captures: 2,
			sessionCloses: 1,
			starts: 2,
			interrupts: 2,
		});
	});

	it("settles stop before a non-cooperative open and closes the late session once", async () => {
		let resolveOpen: ((session: VoiceActivityUtteranceSession) => void) | undefined;
		let sessionCloses = 0;
		let sourceCloses = 0;
		const opening = new Promise<VoiceActivityUtteranceSession>((resolve) => {
			resolveOpen = resolve;
		});
		const start = vi.fn(() => turn("unexpected"));
		const interrupt = vi.fn(async (coordinate: RealtimeVoiceCoordinate) => ({ accepted: true as const, coordinate }));
		const session = new GameContinuousLocalSpeechSession({
			voice: { start, interrupt },
			endpointing: { openSession: () => opening },
			maxPendingTurns: 1,
		});
		const handle = session.start({
			...baseRequest(),
			audio: trackedAudio(() => {
				sourceCloses += 1;
			}),
		});

		handle.stop();
		await expect(handle.result).resolves.toEqual({ status: "stopped" });
		resolveOpen?.({
			captureNext: async () => undefined,
			close: () => {
				sessionCloses += 1;
			},
		});
		await Promise.resolve();
		await Promise.resolve();

		expect({
			sessionCloses,
			sourceCloses,
			starts: start.mock.calls.length,
			interrupts: interrupt.mock.calls.length,
		}).toEqual({
			sessionCloses: 1,
			sourceCloses: 1,
			starts: 0,
			interrupts: 0,
		});
	});

	it("keeps a stale stop scoped to its old exact coordinate after a newer session starts", async () => {
		let activeCoordinate: RealtimeVoiceCoordinate | undefined;
		const interrupt = vi.fn(async (expected: RealtimeVoiceCoordinate) => {
			if (!activeCoordinate) return { accepted: false as const, reason: "no-active-turn" as const };
			if (!sameCoordinate(activeCoordinate, expected)) {
				return { accepted: false as const, reason: "coordinate-mismatch" as const, activeCoordinate };
			}
			const coordinate = activeCoordinate;
			activeCoordinate = undefined;
			return { accepted: true as const, coordinate };
		});
		const voice = {
			start: (request: { coordinate: RealtimeVoiceCoordinate }) => {
				activeCoordinate = request.coordinate;
				return turn(request.coordinate.utteranceId);
			},
			interrupt,
		};
		const makeSession = () =>
			new GameContinuousLocalSpeechSession({
				voice,
				endpointing: {
					openSession: async ({ signal }) => {
						let captures = 0;
						return {
							async captureNext(request = {}) {
								captures += 1;
								if (captures === 1) {
									await request.onSpeechStart?.();
									return utterance(1);
								}
								return await new Promise((_, reject) => {
									signal.addEventListener("abort", () => reject(signal.reason), { once: true });
								});
							},
							close: () => undefined,
						};
					},
				},
				maxPendingTurns: 1,
			});
		const oldSession = makeSession();
		const oldHandle = oldSession.start({
			...baseRequest(),
			createCoordinate: () => coordinate("old", "1"),
		});
		const oldTurns = oldHandle.turns[Symbol.asyncIterator]();
		await oldTurns.next();
		const newSession = makeSession();
		const newHandle = newSession.start({
			...baseRequest(),
			createCoordinate: () => coordinate("new", "2"),
		});
		const newTurns = newHandle.turns[Symbol.asyncIterator]();
		await newTurns.next();

		oldHandle.stop();
		await expect(oldHandle.result).resolves.toEqual({ status: "stopped" });
		expect(activeCoordinate).toMatchObject({ epoch: "2", utteranceId: "new" });
		await expect(interrupt.mock.results[0]?.value).resolves.toMatchObject({
			accepted: false,
			reason: "coordinate-mismatch",
		});

		newHandle.stop();
		await expect(newHandle.result).resolves.toEqual({ status: "stopped" });
		expect(activeCoordinate).toBeUndefined();
		expect(interrupt).toHaveBeenCalledTimes(2);
	});
});

function utterance(value: number) {
	return {
		format: INPUT_FORMAT,
		frameCount: 1,
		byteLength: 2,
		audio: audio(value),
	};
}

function audio(...values: number[]): AsyncIterable<Readonly<RealtimeAudioChunk>> {
	return {
		async *[Symbol.asyncIterator]() {
			for (const value of values) yield { bytes: Uint8Array.of(value, 0) };
		},
	};
}

function trackedAudio(onReturn: () => void): AsyncIterable<Readonly<RealtimeAudioChunk>> {
	return {
		[Symbol.asyncIterator]() {
			return {
				next: () => new Promise<IteratorResult<Readonly<RealtimeAudioChunk>>>(() => undefined),
				return: async () => {
					onReturn();
					return { done: true as const, value: undefined };
				},
			};
		},
	};
}

function turn(id: string): RealtimeVoiceTurnHandle {
	const coordinate = {
		sessionId: "session",
		actorId: "actor",
		voiceSessionId: "voice",
		epoch: "1",
		utteranceId: id,
	};
	return {
		coordinate,
		events: audio() as AsyncIterable<never>,
		completion: Promise.resolve({ status: "completed", coordinate, agentStatus: "completed" }),
	};
}

function baseRequest() {
	return {
		inputFormat: INPUT_FORMAT,
		outputFormat: OUTPUT_FORMAT,
		voiceProfileId: "guard",
		audio: audio(),
		createCoordinate: (utteranceIndex: number) => ({
			sessionId: "session",
			actorId: "actor",
			voiceSessionId: "voice",
			epoch: String(utteranceIndex),
			utteranceId: `u${utteranceIndex}`,
		}),
	};
}

function coordinate(utteranceId: string, epoch: string): RealtimeVoiceCoordinate {
	return { sessionId: "session", actorId: "actor", voiceSessionId: "voice", epoch, utteranceId };
}

function sameCoordinate(left: RealtimeVoiceCoordinate, right: RealtimeVoiceCoordinate): boolean {
	return (
		left.sessionId === right.sessionId &&
		left.actorId === right.actorId &&
		left.voiceSessionId === right.voiceSessionId &&
		left.epoch === right.epoch &&
		left.utteranceId === right.utteranceId
	);
}

async function collect<T>(source: AsyncIterable<T>): Promise<T[]> {
	const values: T[] = [];
	for await (const value of source) values.push(value);
	return values;
}
