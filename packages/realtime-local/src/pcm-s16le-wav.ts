import { raceOperationWithAbort } from "@opengameagent/protocol";
import type { RealtimeAudioChunk, RealtimeAudioFormat } from "@opengameagent/realtime";
import { LocalSpeechCapacityError, LocalSpeechConfigurationError, LocalSpeechProtocolError } from "./errors.js";
import { captureAsyncIterator, captureIteratorResultWithAbort, closeIterator } from "./iterator.js";
import {
	addBounded,
	assertPositiveSafeInteger,
	assertWavCapacity,
	capturePcmS16LeFormat,
	inspectBytesWithAbort,
	snapshotBytes,
} from "./validation.js";
import type { PcmS16LeAudioFormat } from "./types.js";

export interface PcmS16LeWavRequest {
	readonly format: Readonly<RealtimeAudioFormat>;
	readonly audio: AsyncIterable<Readonly<RealtimeAudioChunk>>;
	readonly signal: AbortSignal;
}

/** Every value is mandatory host configuration; this utility supplies no workload defaults. */
export interface PcmS16LeWavLimits {
	readonly maxChunks: number;
	readonly maxChunkBytes: number;
	readonly maxAudioBytes: number;
}

interface CapturedPcmS16LeWavRequest {
	readonly format: Readonly<PcmS16LeAudioFormat>;
	readonly audio: AsyncIterable<Readonly<RealtimeAudioChunk>>;
	readonly signal: AbortSignal;
}

export async function collectPcmS16LeWav(
	request: Readonly<PcmS16LeWavRequest>,
	limits: Readonly<PcmS16LeWavLimits>,
): Promise<Uint8Array> {
	const captured = captureRequest(request);
	const bounds = captureLimits(limits);
	const sampleRate = captured.format.sampleRateHz;
	const channels = captured.format.channelCount;
	if (sampleRate > 0xffff_ffff || channels > 0xffff) throw new LocalSpeechProtocolError();
	const blockAlign = channels * 2;
	const byteRate = sampleRate * blockAlign;
	if (
		!Number.isSafeInteger(blockAlign) ||
		blockAlign > 0xffff ||
		!Number.isSafeInteger(byteRate) ||
		byteRate > 0xffff_ffff
	) {
		throw new LocalSpeechProtocolError();
	}
	let iterator: ReturnType<typeof captureAsyncIterator<Readonly<RealtimeAudioChunk>>>;
	try {
		iterator = captureAsyncIterator<Readonly<RealtimeAudioChunk>>(captured.audio);
	} catch (error) {
		captured.signal.throwIfAborted();
		throw error;
	}
	const parts: Uint8Array[] = [];
	let chunks = 0;
	let bytes = 0;
	try {
		while (true) {
			const result = captureIteratorResultWithAbort<Readonly<RealtimeAudioChunk>>(
				await raceOperationWithAbort(() => iterator.next(), captured.signal),
				captured.signal,
			);
			if (result.done) break;
			chunks += 1;
			if (chunks > bounds.maxChunks) throw new LocalSpeechCapacityError();
			const view = inspectBytesWithAbort(result.value, captured.signal);
			if (view.byteLength > bounds.maxChunkBytes) throw new LocalSpeechCapacityError();
			if (view.byteLength % blockAlign !== 0) throw new LocalSpeechProtocolError();
			bytes = addBounded(bytes, view.byteLength, bounds.maxAudioBytes);
			if (view.byteLength > 0) parts.push(snapshotBytes(view));
		}
	} finally {
		closeIterator(iterator);
	}
	captured.signal.throwIfAborted();
	if (bytes === 0) throw new LocalSpeechProtocolError();
	const wav = new Uint8Array(44 + bytes);
	writeAscii(wav, 0, "RIFF");
	const header = new DataView(wav.buffer);
	header.setUint32(4, 36 + bytes, true);
	writeAscii(wav, 8, "WAVE");
	writeAscii(wav, 12, "fmt ");
	header.setUint32(16, 16, true);
	header.setUint16(20, 1, true);
	header.setUint16(22, channels, true);
	header.setUint32(24, sampleRate, true);
	header.setUint32(28, byteRate, true);
	header.setUint16(32, blockAlign, true);
	header.setUint16(34, 16, true);
	writeAscii(wav, 36, "data");
	header.setUint32(40, bytes, true);
	let offset = 44;
	for (const part of parts) {
		wav.set(part, offset);
		offset += part.byteLength;
	}
	return wav;
}

function captureRequest(request: Readonly<PcmS16LeWavRequest>): Readonly<CapturedPcmS16LeWavRequest> {
	if (!request || typeof request !== "object") throw new LocalSpeechProtocolError();
	let signal: AbortSignal;
	let format: unknown;
	let audio: AsyncIterable<Readonly<RealtimeAudioChunk>>;
	try {
		signal = request.signal;
	} catch {
		throw new LocalSpeechProtocolError();
	}
	if (!(signal instanceof AbortSignal)) throw new LocalSpeechProtocolError();
	signal.throwIfAborted();
	try {
		format = request.format;
		audio = request.audio;
	} catch {
		signal.throwIfAborted();
		throw new LocalSpeechProtocolError();
	}
	const capturedFormat = capturePcmS16LeFormat(format);
	signal.throwIfAborted();
	return Object.freeze({ format: capturedFormat, audio, signal });
}

function captureLimits(limits: Readonly<PcmS16LeWavLimits>): Readonly<PcmS16LeWavLimits> {
	if (!limits || typeof limits !== "object") throw new LocalSpeechConfigurationError();
	let maxChunks: number;
	let maxChunkBytes: number;
	let maxAudioBytes: number;
	try {
		maxChunks = limits.maxChunks;
		maxChunkBytes = limits.maxChunkBytes;
		maxAudioBytes = limits.maxAudioBytes;
	} catch {
		throw new LocalSpeechConfigurationError();
	}
	assertPositiveSafeInteger(maxChunks);
	assertPositiveSafeInteger(maxChunkBytes);
	assertPositiveSafeInteger(maxAudioBytes);
	assertWavCapacity(maxAudioBytes);
	return Object.freeze({ maxChunks, maxChunkBytes, maxAudioBytes });
}

function writeAscii(target: Uint8Array, offset: number, text: string): void {
	for (let index = 0; index < text.length; index += 1) target[offset + index] = text.charCodeAt(index);
}
