import type { RealtimeAudioFrame, RealtimeConversationEvent } from "@opengameagent/realtime";
import { describe, expect, it } from "vitest";
import {
	ComposableLocalSpeechTransport,
	EnergyVoiceActivityDetector,
	type LocalSpeechRecognizer,
	type LocalSpeechSynthesizer,
	type LocalVoiceActivityDetector,
} from "./local-speech.js";
import { OpenAICompatibleSpeechRecognizer, OpenAICompatibleSpeechSynthesizer } from "./openai-compatible-speech.js";

function pcm(...samples: number[]): Uint8Array {
	const bytes = new Uint8Array(samples.length * 2);
	const view = new DataView(bytes.buffer);
	for (let index = 0; index < samples.length; index += 1) view.setInt16(index * 2, samples[index] ?? 0, true);
	return bytes;
}

function wav(audio: Uint8Array, sampleRate = 24_000, channels = 1): Uint8Array {
	const result = new Uint8Array(44 + audio.byteLength);
	const view = new DataView(result.buffer);
	result.set(Buffer.from("RIFF", "ascii"), 0);
	view.setUint32(4, result.byteLength - 8, true);
	result.set(Buffer.from("WAVEfmt ", "ascii"), 8);
	view.setUint32(16, 16, true);
	view.setUint16(20, 1, true);
	view.setUint16(22, channels, true);
	view.setUint32(24, sampleRate, true);
	view.setUint32(28, sampleRate * channels * 2, true);
	view.setUint16(32, channels * 2, true);
	view.setUint16(34, 16, true);
	result.set(Buffer.from("data", "ascii"), 36);
	view.setUint32(40, audio.byteLength, true);
	result.set(audio, 44);
	return result;
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

describe("OpenAI-compatible local speech", () => {
	it("sends a bounded PCM16 WAV multipart transcription request", async () => {
		let form: FormData | undefined;
		let authorization: string | null = null;
		const recognizer = new OpenAICompatibleSpeechRecognizer({
			model: "whisper-local",
			authentication: { resolve: async () => ({ apiKey: "local-secret" }) },
			fetch: async (_input, init) => {
				form = init?.body as FormData;
				authorization = new Headers(init?.headers).get("authorization");
				return new Response(JSON.stringify({ text: "hello world" }), { status: 200 });
			},
		});
		const result = await recognizer.transcribe({
			pcm16: pcm(100, -100),
			sampleRate: 16_000,
			channels: 1,
			language: "en",
		});
		expect(result.text).toBe("hello world");
		expect(form?.get("model")).toBe("whisper-local");
		expect(form?.get("language")).toBe("en");
		const file = form?.get("file") as Blob;
		expect(
			Buffer.from(await file.arrayBuffer())
				.subarray(0, 12)
				.toString("ascii"),
		).toBe("RIFF(\u0000\u0000\u0000WAVE");
		expect(authorization).toBe("Bearer local-secret");
	});

	it("streams raw PCM across arbitrary HTTP chunk boundaries and parses WAV when configured", async () => {
		const raw = new ReadableStream<Uint8Array>({
			start(controller) {
				controller.enqueue(new Uint8Array([0, 0, 1]));
				controller.enqueue(new Uint8Array([0, 2, 0]));
				controller.close();
			},
		});
		const rawSynthesizer = new OpenAICompatibleSpeechSynthesizer({
			model: "tts-local",
			audioFrameBytes: 4,
			fetch: async () => new Response(raw, { status: 200, headers: { "content-type": "audio/pcm" } }),
		});
		const rawFrames: RealtimeAudioFrame[] = [];
		for await (const frame of rawSynthesizer.synthesize({ text: "hello", voice: "voice", itemId: "item" }))
			rawFrames.push(frame);
		expect(rawFrames.map((frame) => [...frame.pcm16])).toEqual([
			[0, 0, 1, 0],
			[2, 0],
		]);

		const waveSynthesizer = new OpenAICompatibleSpeechSynthesizer({
			model: "tts-local",
			responseFormat: "wav",
			audioFrameBytes: 4,
			fetch: async () =>
				new Response(wav(pcm(1, 2, 3), 16_000), { status: 200, headers: { "content-type": "audio/wav" } }),
		});
		const waveFrames: RealtimeAudioFrame[] = [];
		for await (const frame of waveSynthesizer.synthesize({ text: "hello", voice: "voice", itemId: "item" }))
			waveFrames.push(frame);
		expect(waveFrames.map((frame) => frame.sampleRate)).toEqual([16_000, 16_000]);
	});

	it("rejects remote endpoints, invalid media, and secret-bearing provider errors", async () => {
		expect(() => new OpenAICompatibleSpeechRecognizer({ model: "x", endpoint: "https://remote.test/v1" })).toThrow(
			"loopback",
		);
		const recognizer = new OpenAICompatibleSpeechRecognizer({
			model: "x",
			fetch: async () => new Response("secret provider body", { status: 500 }),
		});
		await expect(recognizer.transcribe({ pcm16: pcm(1), sampleRate: 16_000, channels: 1 })).rejects.toThrow("HTTP 500");
	});
});

describe("ComposableLocalSpeechTransport", () => {
	it("turns VAD-delimited PCM into a transcript handoff", async () => {
		const decisions = [true, false, false];
		const vad: LocalVoiceActivityDetector = { isSpeech: () => decisions.shift() ?? false };
		let captured: Uint8Array | undefined;
		const recognizer: LocalSpeechRecognizer = {
			transcribe: async (request) => {
				captured = request.pcm16;
				return { text: "build a house" };
			},
		};
		const synthesizer: LocalSpeechSynthesizer = { synthesize: async function* () {} };
		const session = await new ComposableLocalSpeechTransport({
			recognizer,
			synthesizer,
			voiceActivityDetector: vad,
			silenceFramesToEnd: 2,
		}).connect({ model: "local", voice: "voice" });
		await session.sendAudio({ pcm16: pcm(10), sampleRate: 16_000, channels: 1 });
		await session.sendAudio({ pcm16: pcm(0), sampleRate: 16_000, channels: 1 });
		await session.sendAudio({ pcm16: pcm(0), sampleRate: 16_000, channels: 1 });
		const events = await take(session, 4);
		expect(events.map((event) => event.type)).toEqual([
			"input.speech.started",
			"input.speech.stopped",
			"input.transcript.completed",
			"handoff.requested",
		]);
		expect(captured?.byteLength).toBe(6);
		expect(events[3]).toMatchObject({ handoff: { transcript: "build a house" } });
		await session.close();
	});

	it("streams synthesized audio, supports cancellation, and closes idempotently", async () => {
		const recognizer: LocalSpeechRecognizer = { transcribe: async () => ({ text: "unused" }) };
		const synthesizer: LocalSpeechSynthesizer = {
			synthesize: async function* (request, signal) {
				yield { pcm16: pcm(1), sampleRate: 24_000, channels: 1, itemId: request.itemId };
				await new Promise<void>((resolve) => signal?.addEventListener("abort", () => resolve(), { once: true }));
				signal?.throwIfAborted();
			},
		};
		const session = await new ComposableLocalSpeechTransport({ recognizer, synthesizer }).connect({
			model: "local",
			voice: "npc-voice",
		});
		const handoff = session.sendHandoff("handoff", "hello", "final", true);
		const initial = await take(session, 2);
		expect(initial.map((event) => event.type)).toEqual(["response.started", "output.audio"]);
		await session.cancelResponse();
		await handoff;
		expect((await take(session, 1))[0]?.type).toBe("response.cancelled");
		await expect(Promise.all([session.close(), session.close()])).resolves.toBeDefined();
	});

	it("provides a deterministic no-model energy VAD fallback", () => {
		const vad = new EnergyVoiceActivityDetector({ minimumRootMeanSquare: 0.1 });
		expect(vad.isSpeech({ pcm16: pcm(0, 0), sampleRate: 16_000, channels: 1 })).toBe(false);
		expect(vad.isSpeech({ pcm16: pcm(20_000, -20_000), sampleRate: 16_000, channels: 1 })).toBe(true);
	});
});
