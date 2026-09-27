import { describe, expect, it, vi } from "vitest";
import {
	collectPcmS16LeWav,
	LocalSpeechCapacityError,
	LocalSpeechConfigurationError,
	LocalSpeechProtocolError,
	PCM_S16LE_MEDIA_TYPE,
} from "../src/index.js";

const FORMAT = Object.freeze({ mediaType: PCM_S16LE_MEDIA_TYPE, sampleRateHz: 16_000, channelCount: 1 });
const LIMITS = Object.freeze({ maxChunks: 2, maxChunkBytes: 4, maxAudioBytes: 6 });

describe("collectPcmS16LeWav", () => {
	it("preserves cancellation raised by source cleanup", async () => {
		const controller = new AbortController();
		const reason = new Error("cancel-from-audio-return");
		let reads = 0;
		const audio = {
			[Symbol.asyncIterator]() {
				return {
					async next() {
						reads += 1;
						return reads === 1
							? { done: false as const, value: { bytes: new Uint8Array([1, 2]) } }
							: { done: true as const, value: undefined };
					},
					async return() {
						controller.abort(reason);
						return { done: true as const, value: undefined };
					},
				};
			},
		};

		await expect(
			collectPcmS16LeWav(
				{
					format: { mediaType: PCM_S16LE_MEDIA_TYPE, sampleRateHz: 16_000, channelCount: 1 },
					audio,
					signal: controller.signal,
				},
				{ maxChunks: 1, maxChunkBytes: 2, maxAudioBytes: 2 },
			),
		).rejects.toBe(reason);
	});

	it("closes a non-cooperative source exactly once when cancelled and ignores its late output", async () => {
		const controller = new AbortController();
		const reason = new Error("cancel-pending-audio");
		let settle: ((result: IteratorResult<{ bytes: Uint8Array }>) => void) | undefined;
		const close = vi.fn(async () => ({ done: true as const, value: undefined }));
		const audio = {
			[Symbol.asyncIterator]() {
				return {
					next: vi.fn(
						() =>
							new Promise<IteratorResult<{ bytes: Uint8Array }>>((resolve) => {
								settle = resolve;
								controller.abort(reason);
							}),
					),
					return: close,
				};
			},
		};

		await expect(collectPcmS16LeWav({ format: FORMAT, audio, signal: controller.signal }, LIMITS)).rejects.toBe(reason);
		expect(close).toHaveBeenCalledOnce();
		settle?.({ done: false, value: { bytes: new Uint8Array([1, 2]) } });
		await Promise.resolve();
		expect(close).toHaveBeenCalledOnce();
	});

	it.each([
		{
			name: "chunk count",
			audio: chunks([new Uint8Array([1, 2]), new Uint8Array([3, 4]), new Uint8Array([5, 6])]),
			limits: LIMITS,
		},
		{
			name: "per-chunk bytes",
			audio: chunks([new Uint8Array([1, 2, 3, 4, 5, 6])]),
			limits: LIMITS,
		},
		{
			name: "aggregate bytes",
			audio: chunks([new Uint8Array([1, 2, 3, 4]), new Uint8Array([5, 6, 7, 8])]),
			limits: LIMITS,
		},
	])("reports $name overflow as capacity", async ({ audio, limits }) => {
		await expect(
			collectPcmS16LeWav({ format: FORMAT, audio, signal: new AbortController().signal }, limits),
		).rejects.toBeInstanceOf(LocalSpeechCapacityError);
	});

	it.each([
		{ name: "empty audio", values: [] },
		{ name: "unaligned PCM16 audio", values: [new Uint8Array([1])] },
	])("reports $name as malformed", async ({ values }) => {
		await expect(
			collectPcmS16LeWav({ format: FORMAT, audio: chunks(values), signal: new AbortController().signal }, LIMITS),
		).rejects.toBeInstanceOf(LocalSpeechProtocolError);
	});

	it("rejects an audio budget that cannot fit in a RIFF/WAVE header", async () => {
		await expect(
			collectPcmS16LeWav(
				{ format: FORMAT, audio: chunks([new Uint8Array([1, 2])]), signal: new AbortController().signal },
				{ maxChunks: 1, maxChunkBytes: 2, maxAudioBytes: 0xffff_ffff - 35 },
			),
		).rejects.toBeInstanceOf(LocalSpeechConfigurationError);
	});

	it("collects bounded PCM16 chunks into one deterministic WAV", async () => {
		const wav = await collectPcmS16LeWav(
			{
				format: FORMAT,
				audio: chunks([new Uint8Array([1, 2]), new Uint8Array([3, 4])]),
				signal: new AbortController().signal,
			},
			{ maxChunks: 2, maxChunkBytes: 2, maxAudioBytes: 4 },
		);

		expect(new TextDecoder().decode(wav.subarray(0, 4))).toBe("RIFF");
		expect(new DataView(wav.buffer).getUint32(4, true)).toBe(40);
		expect(new TextDecoder().decode(wav.subarray(8, 12))).toBe("WAVE");
		expect(new DataView(wav.buffer).getUint16(20, true)).toBe(1);
		expect(new DataView(wav.buffer).getUint16(22, true)).toBe(1);
		expect(new DataView(wav.buffer).getUint32(24, true)).toBe(16_000);
		expect(new DataView(wav.buffer).getUint32(28, true)).toBe(32_000);
		expect(new DataView(wav.buffer).getUint16(32, true)).toBe(2);
		expect(new DataView(wav.buffer).getUint16(34, true)).toBe(16);
		expect(new DataView(wav.buffer).getUint32(40, true)).toBe(4);
		expect([...wav.subarray(44)]).toEqual([1, 2, 3, 4]);
	});
});

async function* chunks(values: readonly Uint8Array[]) {
	for (const bytes of values) yield { bytes };
}
