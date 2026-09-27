import { Buffer } from "node:buffer";
import { TextDecoder } from "node:util";
import { gunzipSync, gzipSync } from "node:zlib";
import {
	canonicalizeJsonValueWithin,
	JsonValueLimitError,
	parseStrictJsonText,
	type JsonValue,
} from "@opengameagent/protocol";
import {
	DoubaoRealtimeSpeechAdapterError,
	type DoubaoAsrLimits,
	type DoubaoRealtimeSpeechTransportLimits,
	type DoubaoTtsLimits,
	type DoubaoWebSocketMessage,
} from "./doubao-realtime-speech-types.js";

const FATAL_UTF8_DECODER = new TextDecoder("utf-8", { fatal: true });

export interface DoubaoAsrConfiguration {
	readonly userId: string;
	readonly sampleRateHz: number;
	readonly channelCount: number;
	readonly language: string;
	readonly modelName: string;
}

export interface DoubaoAsrResponse {
	readonly code: number;
	readonly last: boolean;
	readonly sequence?: number;
	readonly text?: string;
}

export interface DoubaoTtsResponse {
	readonly messageType: number;
	readonly event: number;
	readonly sessionId: string;
	readonly payload: Uint8Array;
}

export function createDoubaoTtsEventFrame(
	event: number,
	sessionId: string,
	payload: unknown,
	limits: Readonly<DoubaoRealtimeSpeechTransportLimits>,
): Uint8Array {
	const hasSession = event !== 1 && event !== 2;
	const sessionBytes = hasSession ? Buffer.byteLength(sessionId, "utf8") : 0;
	const frameOverheadBytes = hasSession ? 16 + sessionBytes : 12;
	const body = encodeBoundedJson(
		payload,
		limits,
		Math.min(limits.maxPayloadBytes, limits.maxFrameBytes - frameOverheadBytes),
	);
	const session = hasSession ? Buffer.from(sessionId, "utf8") : undefined;
	const frame = Buffer.allocUnsafe(frameOverheadBytes + body.byteLength);
	frame[0] = 0x11;
	frame[1] = 0x14;
	frame[2] = 0x10;
	frame[3] = 0;
	frame.writeInt32BE(event, 4);
	let offset = 8;
	if (session) {
		frame.writeUInt32BE(sessionBytes, offset);
		offset += 4;
		session.copy(frame, offset);
		offset += sessionBytes;
	}
	frame.writeUInt32BE(body.byteLength, offset);
	body.copy(frame, offset + 4);
	return frame;
}

export function parseDoubaoTtsResponse(
	message: Readonly<DoubaoWebSocketMessage>,
	limits: Readonly<DoubaoTtsLimits>,
): DoubaoTtsResponse {
	if (message.type === "closed") throw new DoubaoRealtimeSpeechAdapterError("transport");
	if (message.type !== "binary") throw new DoubaoRealtimeSpeechAdapterError("protocol");
	const frame = Buffer.from(message.bytes.buffer, message.bytes.byteOffset, message.bytes.byteLength);
	if (frame.byteLength > limits.maxFrameBytes) throw new DoubaoRealtimeSpeechAdapterError("capacity");
	if (frame.byteLength < 12) throw new DoubaoRealtimeSpeechAdapterError("protocol");
	const firstHeader = frame.readUInt8(0);
	const secondHeader = frame.readUInt8(1);
	const thirdHeader = frame.readUInt8(2);
	const headerWords = firstHeader & 0x0f;
	const headerBytes = headerWords * 4;
	const messageType = secondHeader >> 4;
	const flags = secondHeader & 0x0f;
	if (firstHeader >> 4 !== 1 || headerWords < 1 || flags !== 4 || (messageType !== 9 && messageType !== 11)) {
		throw new DoubaoRealtimeSpeechAdapterError("protocol");
	}
	if ((messageType === 9 && thirdHeader !== 0x10) || (messageType === 11 && thirdHeader !== 0)) {
		throw new DoubaoRealtimeSpeechAdapterError("protocol");
	}
	let offset = headerBytes;
	if (offset + 4 > frame.byteLength) throw new DoubaoRealtimeSpeechAdapterError("protocol");
	const event = frame.readInt32BE(offset);
	offset += 4;
	if (offset + 4 > frame.byteLength) throw new DoubaoRealtimeSpeechAdapterError("protocol");
	const idBytes = frame.readUInt32BE(offset);
	offset += 4;
	if (idBytes > limits.maxIdentifierBytes || offset + idBytes + 4 > frame.byteLength) {
		throw new DoubaoRealtimeSpeechAdapterError("protocol");
	}
	const id = decodeUtf8(frame.subarray(offset, offset + idBytes));
	offset += idBytes;
	const payloadBytes = frame.readUInt32BE(offset);
	offset += 4;
	if (payloadBytes > limits.maxPayloadBytes) throw new DoubaoRealtimeSpeechAdapterError("capacity");
	if (offset + payloadBytes !== frame.byteLength) throw new DoubaoRealtimeSpeechAdapterError("protocol");
	const payload = frame.subarray(offset);
	if (messageType === 9) {
		try {
			parseStrictJsonText(decodeUtf8(payload), limits.maxJsonDepth);
		} catch {
			throw new DoubaoRealtimeSpeechAdapterError("protocol");
		}
	}
	const connectionEvent = event === 50 || event === 51 || event === 52;
	return { messageType, event, sessionId: connectionEvent ? "" : id, payload };
}

export function createDoubaoAsrConfigFrame(
	configuration: Readonly<DoubaoAsrConfiguration>,
	limits: Readonly<DoubaoRealtimeSpeechTransportLimits>,
): Uint8Array {
	return createDoubaoAsrFrame(
		1,
		1,
		encodeBoundedJson(
			{
				user: { uid: configuration.userId },
				audio: {
					format: "pcm",
					codec: "raw",
					rate: configuration.sampleRateHz,
					bits: 16,
					channel: configuration.channelCount,
					language: configuration.language,
				},
				request: { model_name: configuration.modelName, show_utterances: true, result_type: "single" },
			},
			limits,
			limits.maxPayloadBytes,
		),
		false,
		limits,
	);
}

export function createDoubaoAsrAudioFrame(
	sequence: number,
	payload: Uint8Array,
	last: boolean,
	limits: Readonly<DoubaoRealtimeSpeechTransportLimits>,
): Uint8Array {
	return createDoubaoAsrFrame(2, last ? -sequence : sequence, payload, last, limits);
}

export function parseDoubaoAsrResponse(bytes: Uint8Array, limits: Readonly<DoubaoAsrLimits>): DoubaoAsrResponse {
	if (bytes.byteLength > limits.maxFrameBytes) throw new DoubaoRealtimeSpeechAdapterError("capacity");
	if (bytes.byteLength < 8) throw new DoubaoRealtimeSpeechAdapterError("protocol");
	const frame = Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength);
	const firstHeader = frame.readUInt8(0);
	const secondHeader = frame.readUInt8(1);
	const thirdHeader = frame.readUInt8(2);
	const headerWords = firstHeader & 0x0f;
	const headerBytes = headerWords * 4;
	const messageType = secondHeader >> 4;
	const flags = secondHeader & 0x0f;
	if (firstHeader >> 4 !== 1 || headerWords < 1 || headerBytes > frame.byteLength || flags > 3) {
		throw new DoubaoRealtimeSpeechAdapterError("protocol");
	}
	let offset = headerBytes;
	let sequence: number | undefined;
	if ((flags & 1) !== 0) {
		if (offset + 4 > frame.byteLength) throw new DoubaoRealtimeSpeechAdapterError("protocol");
		sequence = frame.readInt32BE(offset);
		offset += 4;
	}
	let code = 0;
	if (messageType === 15) {
		if (offset + 8 > frame.byteLength) throw new DoubaoRealtimeSpeechAdapterError("protocol");
		code = frame.readInt32BE(offset);
		offset += 4;
	} else if (messageType !== 9) throw new DoubaoRealtimeSpeechAdapterError("protocol");
	if (offset + 4 > frame.byteLength) throw new DoubaoRealtimeSpeechAdapterError("protocol");
	const size = frame.readUInt32BE(offset);
	offset += 4;
	if (size > limits.maxPayloadBytes) throw new DoubaoRealtimeSpeechAdapterError("capacity");
	if (offset + size !== frame.byteLength) throw new DoubaoRealtimeSpeechAdapterError("protocol");
	if (size === 0) return { code, last: (flags & 2) !== 0, ...(sequence === undefined ? {} : { sequence }) };
	let payload = frame.subarray(offset);
	if ((thirdHeader & 0x0f) === 1) {
		try {
			payload = gunzipSync(payload, { maxOutputLength: limits.maxPayloadBytes });
		} catch (error) {
			if ((error as { readonly code?: unknown })?.code === "ERR_BUFFER_TOO_LARGE") {
				throw new DoubaoRealtimeSpeechAdapterError("capacity");
			}
			throw new DoubaoRealtimeSpeechAdapterError("protocol");
		}
	} else if ((thirdHeader & 0x0f) !== 0) throw new DoubaoRealtimeSpeechAdapterError("protocol");
	if (payload.byteLength > limits.maxPayloadBytes) throw new DoubaoRealtimeSpeechAdapterError("capacity");
	if (thirdHeader >> 4 !== 1) throw new DoubaoRealtimeSpeechAdapterError("protocol");
	let value: JsonValue;
	try {
		value = parseStrictJsonText(decodeUtf8(payload), limits.maxJsonDepth);
	} catch {
		throw new DoubaoRealtimeSpeechAdapterError("protocol");
	}
	const text = readAsrText(value);
	return {
		code,
		last: (flags & 2) !== 0,
		...(sequence === undefined ? {} : { sequence }),
		...(text === undefined ? {} : { text }),
	};
}

function decodeUtf8(value: Uint8Array): string {
	try {
		return FATAL_UTF8_DECODER.decode(value);
	} catch {
		throw new DoubaoRealtimeSpeechAdapterError("protocol");
	}
}

function createDoubaoAsrFrame(
	messageType: number,
	sequence: number,
	payload: Uint8Array,
	last: boolean,
	limits: Readonly<DoubaoRealtimeSpeechTransportLimits>,
): Uint8Array {
	const maxCompressedBytes = Math.min(limits.maxPayloadBytes, limits.maxFrameBytes - 12);
	if (maxCompressedBytes < 1) throw new DoubaoRealtimeSpeechAdapterError("capacity");
	let compressed: Buffer;
	try {
		compressed = gzipSync(payload, { maxOutputLength: maxCompressedBytes });
	} catch (error) {
		if ((error as { readonly code?: unknown })?.code === "ERR_BUFFER_TOO_LARGE") {
			throw new DoubaoRealtimeSpeechAdapterError("capacity");
		}
		throw new DoubaoRealtimeSpeechAdapterError("protocol");
	}
	const frame = Buffer.allocUnsafe(12 + compressed.byteLength);
	frame[0] = 0x11;
	frame[1] = (messageType << 4) | (last ? 3 : 1);
	frame[2] = 0x11;
	frame[3] = 0;
	frame.writeInt32BE(sequence, 4);
	frame.writeUInt32BE(compressed.byteLength, 8);
	compressed.copy(frame, 12);
	return frame;
}

function readAsrText(value: JsonValue): string | undefined {
	if (!value || typeof value !== "object" || Array.isArray(value)) return undefined;
	const result = value.result;
	if (!Array.isArray(result) || result.length === 0) return undefined;
	const latest = result.at(-1);
	if (!latest || typeof latest !== "object" || Array.isArray(latest)) return undefined;
	return typeof latest.text === "string" ? latest.text : undefined;
}

function encodeBoundedJson(
	value: unknown,
	limits: Readonly<DoubaoRealtimeSpeechTransportLimits>,
	maxBytes: number,
): Buffer {
	if (maxBytes < 1) throw new DoubaoRealtimeSpeechAdapterError("capacity");
	try {
		return Buffer.from(canonicalizeJsonValueWithin(value, { maxDepth: limits.maxJsonDepth, maxBytes }), "utf8");
	} catch (error) {
		if (error instanceof JsonValueLimitError) throw new DoubaoRealtimeSpeechAdapterError("capacity");
		if (error instanceof DoubaoRealtimeSpeechAdapterError) throw error;
		throw new DoubaoRealtimeSpeechAdapterError("protocol");
	}
}
