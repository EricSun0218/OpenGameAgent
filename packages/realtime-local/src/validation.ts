import { Buffer, constants as bufferConstants } from "node:buffer";
import type { RealtimeAudioChunk, RealtimeAudioFormat } from "@opengameagent/realtime";
import { LocalSpeechCapacityError, LocalSpeechConfigurationError, LocalSpeechProtocolError } from "./errors.js";
import type { LocalSpeechAudioFormat, PcmS16LeAudioFormat } from "./types.js";
import { PCM_S16LE_MEDIA_TYPE } from "./types.js";

const TYPED_ARRAY_PROTOTYPE = Object.getPrototypeOf(Uint8Array.prototype) as object;
const TYPED_ARRAY_BUFFER = Object.getOwnPropertyDescriptor(TYPED_ARRAY_PROTOTYPE, "buffer")?.get as (
	this: Uint8Array,
) => ArrayBufferLike;
const TYPED_ARRAY_BYTE_LENGTH = Object.getOwnPropertyDescriptor(TYPED_ARRAY_PROTOTYPE, "byteLength")?.get as (
	this: Uint8Array,
) => number;
const TYPED_ARRAY_BYTE_OFFSET = Object.getOwnPropertyDescriptor(TYPED_ARRAY_PROTOTYPE, "byteOffset")?.get as (
	this: Uint8Array,
) => number;
const TYPED_ARRAY_SET = Uint8Array.prototype.set;

export const NODE_MAX_TIMER_DELAY_MS = 2_147_483_647;
export const MAX_WAV_DATA_BYTES = 0xffff_ffff - 36;

export interface ByteView {
	readonly buffer: ArrayBufferLike;
	readonly byteLength: number;
	readonly byteOffset: number;
}

export function inspectBytes(value: unknown): ByteView {
	if (!value || typeof value !== "object") throw new LocalSpeechProtocolError();
	let bytes: unknown;
	try {
		bytes = (value as RealtimeAudioChunk).bytes;
	} catch {
		throw new LocalSpeechProtocolError();
	}
	try {
		return {
			buffer: TYPED_ARRAY_BUFFER.call(bytes as Uint8Array),
			byteLength: TYPED_ARRAY_BYTE_LENGTH.call(bytes as Uint8Array),
			byteOffset: TYPED_ARRAY_BYTE_OFFSET.call(bytes as Uint8Array),
		};
	} catch {
		throw new LocalSpeechProtocolError();
	}
}

export function inspectBytesWithAbort(value: unknown, signal: AbortSignal): ByteView {
	try {
		const view = inspectBytes(value);
		signal.throwIfAborted();
		return view;
	} catch (error) {
		signal.throwIfAborted();
		throw error;
	}
}

export function snapshotBytes(view: Readonly<ByteView>): Uint8Array {
	const snapshot = new Uint8Array(view.byteLength);
	TYPED_ARRAY_SET.call(snapshot, new Uint8Array(view.buffer, view.byteOffset, view.byteLength));
	return snapshot;
}

export function captureFormat(value: unknown, maxMediaTypeBytes: number): Readonly<RealtimeAudioFormat> {
	const format = captureAudioFormat(value);
	if (Buffer.byteLength(format.mediaType, "utf8") > maxMediaTypeBytes) throw new LocalSpeechProtocolError();
	return format;
}

export function captureAudioFormat(value: unknown): Readonly<LocalSpeechAudioFormat> {
	if (!value || typeof value !== "object") throw new LocalSpeechProtocolError();
	let mediaType: unknown;
	let sampleRateHz: unknown;
	let channelCount: unknown;
	try {
		const format = value as RealtimeAudioFormat;
		mediaType = format.mediaType;
		sampleRateHz = format.sampleRateHz;
		channelCount = format.channelCount;
	} catch {
		throw new LocalSpeechProtocolError();
	}
	if (
		typeof mediaType !== "string" ||
		mediaType.length === 0 ||
		containsControl(mediaType) ||
		!isPositiveSafeInteger(sampleRateHz) ||
		!isPositiveSafeInteger(channelCount)
	) {
		throw new LocalSpeechProtocolError();
	}
	return Object.freeze({ mediaType, sampleRateHz, channelCount });
}

export function capturePcmS16LeFormat(value: unknown): Readonly<PcmS16LeAudioFormat> {
	const format = captureAudioFormat(value);
	if (format.mediaType !== PCM_S16LE_MEDIA_TYPE) throw new LocalSpeechProtocolError();
	return format as Readonly<PcmS16LeAudioFormat>;
}

export function sameFormat(left: Readonly<RealtimeAudioFormat>, right: Readonly<RealtimeAudioFormat>): boolean {
	return (
		left.mediaType === right.mediaType &&
		left.sampleRateHz === right.sampleRateHz &&
		left.channelCount === right.channelCount
	);
}

export function captureString(value: unknown): string {
	if (typeof value !== "string" || value.length === 0 || containsControl(value)) {
		throw new LocalSpeechProtocolError();
	}
	return value;
}

export function captureBoundedString(value: unknown, maxBytes: number, configuration = false): string {
	if (
		typeof value !== "string" ||
		value.length === 0 ||
		containsControl(value) ||
		Buffer.byteLength(value, "utf8") > maxBytes
	) {
		if (configuration) throw new LocalSpeechConfigurationError();
		throw new LocalSpeechProtocolError();
	}
	return value;
}

export function utf8Bytes(value: string): number {
	return Buffer.byteLength(value, "utf8");
}

export function assertPositiveSafeInteger(value: unknown): asserts value is number {
	if (!isPositiveSafeInteger(value)) throw new LocalSpeechConfigurationError();
}

export function assertNonNegativeSafeInteger(value: unknown): asserts value is number {
	if (!Number.isSafeInteger(value) || Object.is(value, -0) || (value as number) < 0) {
		throw new LocalSpeechConfigurationError();
	}
}

export function assertTimer(value: unknown): asserts value is number {
	assertPositiveSafeInteger(value);
	if ((value as number) > NODE_MAX_TIMER_DELAY_MS) throw new LocalSpeechConfigurationError();
}

export function assertWavCapacity(value: number): void {
	if (value > MAX_WAV_DATA_BYTES || value > bufferConstants.MAX_LENGTH - 44) {
		throw new LocalSpeechConfigurationError();
	}
}

export function addBounded(current: number, next: number, maximum: number): number {
	if (next > maximum || current > maximum - next) throw new LocalSpeechCapacityError();
	return current + next;
}

export function isPositiveSafeInteger(value: unknown): value is number {
	return Number.isSafeInteger(value) && !Object.is(value, -0) && (value as number) > 0;
}

function containsControl(value: string): boolean {
	for (let index = 0; index < value.length; index += 1) {
		const code = value.charCodeAt(index);
		if (code <= 0x1f || (code >= 0x7f && code <= 0x9f)) return true;
	}
	return false;
}
