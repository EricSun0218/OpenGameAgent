import { gzipSync } from "node:zlib";
import { describe, expect, it } from "vitest";
import { decodeVolcengineFrame, encodeVolcengineFrame, VolcengineEvent, VolcengineMessageType } from "./wire.js";

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

function serverFrame(options: {
	event: number;
	sessionId?: string;
	connectId?: string;
	payload?: Uint8Array;
	gzip?: boolean;
}): Uint8Array {
	const payload = options.gzip
		? new Uint8Array(gzipSync(options.payload ?? new Uint8Array()))
		: (options.payload ?? new Uint8Array());
	const fields: Uint8Array[] = [int32(options.event)];
	for (const value of [options.sessionId, options.connectId]) {
		if (!value) continue;
		const bytes = new TextEncoder().encode(value);
		fields.push(int32(bytes.byteLength), bytes);
	}
	fields.push(int32(payload.byteLength), payload);
	return concat(
		new Uint8Array([0x11, (VolcengineMessageType.fullServerResponse << 4) | 4, (1 << 4) | (options.gzip ? 1 : 0), 0]),
		...fields,
	);
}

describe("Volcengine wire protocol", () => {
	it("encodes client events with bounded big-endian ids and gzip payloads", () => {
		const payload = new TextEncoder().encode(JSON.stringify({ text: "hello" }));
		const frame = encodeVolcengineFrame({
			messageType: VolcengineMessageType.fullClientRequest,
			eventType: VolcengineEvent.taskRequest,
			sessionId: "session-1",
			payload,
			serialization: "json",
			compression: "gzip",
		});
		expect(frame[0]).toBe(0x11);
		expect(frame[1]).toBe(0x14);
		expect(frame[2]).toBe(0x11);
		expect(new DataView(frame.buffer, frame.byteOffset).getInt32(4, false)).toBe(VolcengineEvent.taskRequest);
	});

	it("decodes connection and session server events without losing payload bytes", () => {
		const connection = decodeVolcengineFrame(
			serverFrame({ event: VolcengineEvent.connectionStarted, connectId: "connection-1" }),
			1_024,
		);
		expect(connection).toMatchObject({
			eventType: VolcengineEvent.connectionStarted,
			connectId: "connection-1",
			serialization: "json",
		});

		const content = new TextEncoder().encode(JSON.stringify({ transcript: "hello" }));
		const session = decodeVolcengineFrame(
			serverFrame({ event: VolcengineEvent.asrResponse, sessionId: "session-1", payload: content, gzip: true }),
			1_024,
		);
		expect(session.sessionId).toBe("session-1");
		expect(new TextDecoder().decode(session.payload)).toBe(new TextDecoder().decode(content));
	});

	it("fails closed on truncation, unsupported encoding, and decompression expansion", () => {
		expect(() => decodeVolcengineFrame(new Uint8Array([0x11, 0x94]), 1_024)).toThrow("truncated");
		const invalid = serverFrame({ event: VolcengineEvent.connectionStarted, connectId: "connection-1" });
		invalid[2] = 0x20;
		expect(() => decodeVolcengineFrame(invalid, 1_024)).toThrow("unsupported");
		const expanded = serverFrame({
			event: VolcengineEvent.asrResponse,
			sessionId: "session-1",
			payload: new Uint8Array(4_096),
			gzip: true,
		});
		expect(() => decodeVolcengineFrame(expanded, 1_024)).toThrow();
	});
});
