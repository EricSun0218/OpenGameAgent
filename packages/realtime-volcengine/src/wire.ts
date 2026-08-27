import { gunzipSync, gzipSync } from "node:zlib";

export const VolcengineMessageType = {
	fullClientRequest: 1,
	audioOnlyClient: 2,
	fullServerResponse: 9,
	audioOnlyServer: 11,
	frontEndResultServer: 12,
	error: 15,
} as const;

export const VolcengineEvent = {
	startConnection: 1,
	finishConnection: 2,
	connectionStarted: 50,
	connectionFailed: 51,
	connectionFinished: 52,
	startSession: 100,
	cancelSession: 101,
	finishSession: 102,
	sessionStarted: 150,
	sessionCancelled: 151,
	sessionFinished: 152,
	sessionFailed: 153,
	taskRequest: 200,
	ttsSentenceStart: 350,
	ttsSentenceEnd: 351,
	ttsResponse: 352,
	ttsEnded: 359,
	ttsSubtitle: 364,
	asrInfo: 450,
	asrResponse: 451,
	asrEnded: 459,
	clientInterrupt: 515,
	chatResponse: 550,
	chatEnded: 559,
} as const;

export interface VolcengineWireMessage {
	messageType: number;
	eventType?: number;
	sessionId?: string;
	connectId?: string;
	errorCode?: number;
	serialization: "raw" | "json";
	payload: Uint8Array;
}

function noSession(event: number): boolean {
	return [1, 2, 50, 51, 52].includes(event);
}

function hasConnectId(event: number): boolean {
	return [50, 51, 52].includes(event);
}

function writeInt(target: Uint8Array, offset: number, value: number): number {
	new DataView(target.buffer, target.byteOffset, target.byteLength).setInt32(offset, value, false);
	return offset + 4;
}

function readInt(source: Uint8Array, cursor: { offset: number }, field: string): number {
	if (cursor.offset > source.byteLength - 4) throw new Error(`Volcengine ${field} is truncated.`);
	const value = new DataView(source.buffer, source.byteOffset, source.byteLength).getInt32(cursor.offset, false);
	cursor.offset += 4;
	return value;
}

function encodedId(value: string | undefined, name: string): Uint8Array {
	if (!value || value.length > 512 || [...value].some((character) => (character.codePointAt(0) ?? 0) < 32)) {
		throw new TypeError(`Volcengine ${name} is invalid.`);
	}
	return new TextEncoder().encode(value);
}

function readId(source: Uint8Array, cursor: { offset: number }, name: string): string {
	const length = readInt(source, cursor, `${name} length`);
	if (length < 1 || length > 512 || cursor.offset + length > source.byteLength)
		throw new Error(`Volcengine ${name} is invalid.`);
	const value = new TextDecoder("utf-8", { fatal: true }).decode(
		source.subarray(cursor.offset, cursor.offset + length),
	);
	cursor.offset += length;
	encodedId(value, name);
	return value;
}

export function encodeVolcengineFrame(options: {
	messageType: typeof VolcengineMessageType.fullClientRequest | typeof VolcengineMessageType.audioOnlyClient;
	eventType: number;
	sessionId?: string;
	payload: Uint8Array;
	serialization: "raw" | "json";
	compression?: "none" | "gzip";
}): Uint8Array {
	if (!Number.isInteger(options.eventType) || options.eventType < 1)
		throw new RangeError("Volcengine event type is invalid.");
	const session = noSession(options.eventType) ? undefined : encodedId(options.sessionId, "session id");
	const payload = options.compression === "gzip" ? new Uint8Array(gzipSync(options.payload)) : options.payload;
	const result = new Uint8Array(4 + 4 + (session ? 4 + session.byteLength : 0) + 4 + payload.byteLength);
	result[0] = 0x11;
	result[1] = (options.messageType << 4) | 4;
	result[2] = (options.serialization === "json" ? 1 << 4 : 0) | (options.compression === "gzip" ? 1 : 0);
	let offset = 4;
	offset = writeInt(result, offset, options.eventType);
	if (session) {
		offset = writeInt(result, offset, session.byteLength);
		result.set(session, offset);
		offset += session.byteLength;
	}
	offset = writeInt(result, offset, payload.byteLength);
	result.set(payload, offset);
	return result;
}

export function decodeVolcengineFrame(source: Uint8Array, maximumPayloadBytes: number): VolcengineWireMessage {
	if (source.byteLength < 8) throw new Error("Volcengine frame is truncated.");
	const version = (source[0] ?? 0) >> 4;
	const headerWords = (source[0] ?? 0) & 15;
	if (version !== 1 || headerWords < 1 || headerWords * 4 > source.byteLength)
		throw new Error("Volcengine frame header is invalid.");
	const messageType = (source[1] ?? 0) >> 4;
	if (![9, 11, 12, 15].includes(messageType)) throw new Error("Volcengine server message type is invalid.");
	const flags = (source[1] ?? 0) & 15;
	const serializationValue = (source[2] ?? 0) >> 4;
	const compressionValue = (source[2] ?? 0) & 15;
	if (![0, 1].includes(serializationValue) || ![0, 1].includes(compressionValue))
		throw new Error("Volcengine frame encoding is unsupported.");
	const cursor = { offset: headerWords * 4 };
	let eventType: number | undefined;
	let sessionId: string | undefined;
	let connectId: string | undefined;
	let errorCode: number | undefined;
	if (messageType === 15) {
		errorCode = readInt(source, cursor, "error code");
	} else {
		if ([1, 3].includes(flags & 3)) readInt(source, cursor, "sequence");
		if ((flags & 4) !== 0) {
			eventType = readInt(source, cursor, "event type");
			if (!noSession(eventType)) sessionId = readId(source, cursor, "session id");
			if (hasConnectId(eventType)) connectId = readId(source, cursor, "connection id");
		}
	}
	const length = readInt(source, cursor, "payload length");
	if (length < 0 || length > maximumPayloadBytes || cursor.offset + length !== source.byteLength)
		throw new Error("Volcengine payload length is invalid.");
	const encoded = source.subarray(cursor.offset, cursor.offset + length);
	const payload =
		compressionValue === 1
			? new Uint8Array(gunzipSync(encoded, { maxOutputLength: maximumPayloadBytes }))
			: encoded.slice();
	if (payload.byteLength > maximumPayloadBytes) throw new Error("Volcengine payload is too large.");
	return {
		messageType,
		...(eventType === undefined ? {} : { eventType }),
		...(sessionId === undefined ? {} : { sessionId }),
		...(connectId === undefined ? {} : { connectId }),
		...(errorCode === undefined ? {} : { errorCode }),
		serialization: serializationValue === 1 ? "json" : "raw",
		payload,
	};
}
