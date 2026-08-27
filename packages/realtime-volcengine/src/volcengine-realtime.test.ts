import { gunzipSync } from "node:zlib";
import type { RealtimeConversationEvent } from "@opengameagent/realtime";
import { describe, expect, it, vi } from "vitest";
import {
	VolcengineRealtimeTransport,
	type VolcengineSocket,
	type VolcengineSocketConnectRequest,
	type VolcengineSocketFactory,
} from "./volcengine-realtime.js";
import { VolcengineEvent, VolcengineMessageType } from "./wire.js";

function int32(value: number): Uint8Array {
	const result = new Uint8Array(4);
	new DataView(result.buffer).setInt32(0, value, false);
	return result;
}

function concat(...parts: Uint8Array[]): Uint8Array {
	const result = new Uint8Array(parts.reduce((sum, part) => sum + part.byteLength, 0));
	let offset = 0;
	for (const part of parts) {
		result.set(part, offset);
		offset += part.byteLength;
	}
	return result;
}

function noSession(event: number): boolean {
	return (
		event === VolcengineEvent.startConnection ||
		event === VolcengineEvent.finishConnection ||
		event === VolcengineEvent.connectionStarted ||
		event === VolcengineEvent.connectionFailed ||
		event === VolcengineEvent.connectionFinished
	);
}

function serverFrame(options: {
	event: number;
	sessionId?: string;
	messageType?: number;
	payload?: Uint8Array;
}): Uint8Array {
	const payload = options.payload ?? new Uint8Array();
	const fields: Uint8Array[] = [int32(options.event)];
	if (!noSession(options.event)) {
		const session = new TextEncoder().encode(options.sessionId ?? "session-1");
		fields.push(int32(session.byteLength), session);
	}
	if (options.event === VolcengineEvent.connectionStarted) {
		const connection = new TextEncoder().encode("connection-1");
		fields.push(int32(connection.byteLength), connection);
	}
	fields.push(int32(payload.byteLength), payload);
	return concat(
		new Uint8Array([0x11, ((options.messageType ?? VolcengineMessageType.fullServerResponse) << 4) | 4, 0x10, 0]),
		...fields,
	);
}

interface ClientFrame {
	event: number;
	sessionId?: string;
	payload: Record<string, unknown> | Uint8Array;
}

function readInt(source: Uint8Array, cursor: { value: number }): number {
	const value = new DataView(source.buffer, source.byteOffset, source.byteLength).getInt32(cursor.value, false);
	cursor.value += 4;
	return value;
}

function parseClientFrame(source: Uint8Array): ClientFrame {
	const cursor = { value: 4 };
	const event = readInt(source, cursor);
	let sessionId: string | undefined;
	if (!noSession(event)) {
		const length = readInt(source, cursor);
		sessionId = new TextDecoder().decode(source.subarray(cursor.value, cursor.value + length));
		cursor.value += length;
	}
	const length = readInt(source, cursor);
	const encoded = source.subarray(cursor.value, cursor.value + length);
	const bytes = (source[2] ?? 0) & 15 ? new Uint8Array(gunzipSync(encoded)) : encoded;
	const payload =
		(source[2] ?? 0) >> 4 === 1 ? (JSON.parse(new TextDecoder().decode(bytes)) as Record<string, unknown>) : bytes;
	return { event, ...(sessionId ? { sessionId } : {}), payload };
}

class AsyncBytes {
	private readonly values: Uint8Array[] = [];
	private waiters: Array<() => void> = [];
	private done = false;
	push(value: Uint8Array): void {
		if (this.done) return;
		this.values.push(value);
		for (const wake of this.waiters.splice(0)) wake();
	}
	close(): void {
		this.done = true;
		for (const wake of this.waiters.splice(0)) wake();
	}
	async *read(signal?: AbortSignal): AsyncIterable<Uint8Array> {
		for (;;) {
			const value = this.values.shift();
			if (value) {
				yield value;
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

class FakeSocket implements VolcengineSocket {
	readonly incoming = new AsyncBytes();
	readonly sent: ClientFrame[] = [];
	closed = 0;
	constructor(readonly request: VolcengineSocketConnectRequest) {}
	async send(data: Uint8Array): Promise<void> {
		const frame = parseClientFrame(data);
		this.sent.push(frame);
		if (frame.event === VolcengineEvent.startConnection) this.emit({ event: VolcengineEvent.connectionStarted });
		if (frame.event === VolcengineEvent.startSession) {
			if (!frame.sessionId) throw new Error("Test frame is missing a session id.");
			this.emit({ event: VolcengineEvent.sessionStarted, sessionId: frame.sessionId });
		}
	}
	messages(signal?: AbortSignal): AsyncIterable<Uint8Array> {
		return this.incoming.read(signal);
	}
	async close(): Promise<void> {
		this.closed += 1;
		this.incoming.close();
	}
	emit(options: Parameters<typeof serverFrame>[0]): void {
		this.incoming.push(serverFrame(options));
	}
	remoteClose(): void {
		this.incoming.close();
	}
}

function fakeFactory() {
	const sockets: FakeSocket[] = [];
	const factory: VolcengineSocketFactory = async (request) => {
		const socket = new FakeSocket(request);
		sockets.push(socket);
		return socket;
	};
	return { factory, sockets };
}

async function take(
	session: { events(signal?: AbortSignal): AsyncIterable<RealtimeConversationEvent> },
	count: number,
) {
	const events: RealtimeConversationEvent[] = [];
	for await (const event of session.events()) {
		events.push(event);
		if (events.length === count) break;
	}
	return events;
}

function required(value: string | undefined): string {
	if (!value) throw new Error("Expected a session id.");
	return value;
}

describe("VolcengineRealtimeTransport", () => {
	it("streams bounded PCM16, subtitles, and per-conversation voice through the provider-neutral contract", async () => {
		const { factory, sockets } = fakeFactory();
		const transport = new VolcengineRealtimeTransport({
			inputMode: "disabled",
			authentication: { resolve: async () => ({ apiKey: "secret-key" }) },
			socketFactory: factory,
		});
		const session = await transport.connect({ model: "doubao-speech", voice: "npc-voice" });
		const socket = sockets[0];
		expect(socket?.request.headers).toMatchObject({
			"X-Api-Key": "secret-key",
			"X-Api-Resource-Id": "seed-tts-2.0",
		});
		await session.sendHandoff("handoff-1", "hello", "final", true);
		const start = socket?.sent.find((frame) => frame.event === VolcengineEvent.startSession);
		expect(start?.payload).toMatchObject({ req_params: { speaker: "npc-voice" } });
		expect(socket?.sent.find((frame) => frame.event === VolcengineEvent.taskRequest)?.payload).toEqual({
			req_params: { text: "hello" },
		});
		const sessionId = required(start?.sessionId);
		socket?.emit({ event: VolcengineEvent.ttsSentenceStart, sessionId });
		socket?.emit({
			event: VolcengineEvent.ttsResponse,
			sessionId,
			messageType: VolcengineMessageType.audioOnlyServer,
			payload: new Uint8Array([0, 0, 1, 0]),
		});
		socket?.emit({
			event: VolcengineEvent.ttsSubtitle,
			sessionId,
			payload: new TextEncoder().encode(JSON.stringify({ text: "hello" })),
		});
		socket?.emit({ event: VolcengineEvent.ttsEnded, sessionId });
		const events = await take(session, 5);
		expect(events.map((event) => event.type)).toEqual([
			"response.started",
			"output.audio",
			"output.transcript.delta",
			"output.transcript.completed",
			"response.completed",
		]);
		expect(events[1]).toMatchObject({ audio: { sampleRate: 24_000, channels: 1 } });
		await Promise.all([session.close(), session.close()]);
		expect(socket?.closed).toBe(1);
	});

	it("isolates speaker choice across concurrent NPC sessions", async () => {
		const { factory, sockets } = fakeFactory();
		const transport = new VolcengineRealtimeTransport({
			inputMode: "disabled",
			speaker: "fallback-voice",
			authentication: { resolve: async () => ({ apiKey: "secret" }) },
			socketFactory: factory,
		});
		const [first, second, fallback] = await Promise.all([
			transport.connect({ model: "speech", voice: "voice-a" }),
			transport.connect({ model: "speech", voice: "voice-b" }),
			transport.connect({ model: "speech", voice: "alloy" }),
		]);
		await Promise.all([
			first.sendHandoff("a", "one", "final", false),
			second.sendHandoff("b", "two", "final", false),
			fallback.sendHandoff("c", "three", "final", false),
		]);
		const speakers = sockets.map((socket) => {
			const payload = socket.sent.find((frame) => frame.event === VolcengineEvent.startSession)?.payload as {
				req_params?: { speaker?: string };
			};
			return payload.req_params?.speaker;
		});
		expect(speakers).toEqual(["voice-a", "voice-b", "fallback-voice"]);
		await Promise.all([first.close(), second.close(), fallback.close()]);
	});

	it("maps dialogue speech boundaries and transcripts without giving the speech provider world authority", async () => {
		const { factory, sockets } = fakeFactory();
		const transport = new VolcengineRealtimeTransport({
			authentication: { resolve: async () => ({ apiKey: "secret" }) },
			socketFactory: factory,
		});
		const session = await transport.connect({ model: "speech", voice: "voice" });
		const dialogue = sockets[0];
		const dialogueSession = required(
			dialogue?.sent.find((frame) => frame.event === VolcengineEvent.startSession)?.sessionId,
		);
		await session.sendAudio({ pcm16: new Uint8Array([0, 0, 1, 0]), sampleRate: 16_000, channels: 1 });
		expect(dialogue?.sent.at(-1)).toMatchObject({ event: VolcengineEvent.taskRequest, sessionId: dialogueSession });
		dialogue?.emit({ event: VolcengineEvent.asrInfo, sessionId: dialogueSession });
		dialogue?.emit({
			event: VolcengineEvent.asrResponse,
			sessionId: dialogueSession,
			payload: new TextEncoder().encode(JSON.stringify({ transcript: "build a house" })),
		});
		dialogue?.emit({ event: VolcengineEvent.asrEnded, sessionId: dialogueSession });
		const events = await take(session, 5);
		expect(events.map((event) => event.type)).toEqual([
			"input.speech.started",
			"input.transcript.delta",
			"input.speech.stopped",
			"input.transcript.completed",
			"handoff.requested",
		]);
		expect(events[4]).toMatchObject({ handoff: { transcript: "build a house" } });
		await session.close();
	});

	it("cancels active speech once and closes idempotently after a remote close", async () => {
		const { factory, sockets } = fakeFactory();
		const transport = new VolcengineRealtimeTransport({
			inputMode: "disabled",
			authentication: { resolve: async () => ({ apiKey: "secret" }) },
			socketFactory: factory,
		});
		const session = await transport.connect({ model: "speech", voice: "voice" });
		await session.sendHandoff("handoff", "hello", "commentary", false);
		await Promise.all([session.cancelResponse(), session.cancelResponse()]);
		expect(sockets[0]?.sent.filter((frame) => frame.event === VolcengineEvent.cancelSession)).toHaveLength(1);
		expect((await take(session, 1))[0]?.type).toBe("response.cancelled");
		sockets[0]?.remoteClose();
		expect((await take(session, 1))[0]?.type).toBe("closed");
		await expect(Promise.all([session.close(), session.close()])).resolves.toBeDefined();
	});

	it("fails closed on invalid authority and never exposes provider secrets in errors", async () => {
		const socketFactory = vi.fn<VolcengineSocketFactory>();
		const transport = new VolcengineRealtimeTransport({
			inputMode: "disabled",
			authentication: { resolve: async () => ({ apiKey: "secret", headers: { Authorization: "secret" } }) },
			socketFactory,
		});
		await expect(transport.connect({ model: "speech", voice: "voice" })).rejects.toThrow("unsupported");
		expect(socketFactory).not.toHaveBeenCalled();
		expect(
			() =>
				new VolcengineRealtimeTransport({
					ttsEndpoint: "ws://remote.example.test/speech",
					authentication: { resolve: async () => ({ apiKey: "secret" }) },
				}),
		).toThrow("WSS");
	});
});
