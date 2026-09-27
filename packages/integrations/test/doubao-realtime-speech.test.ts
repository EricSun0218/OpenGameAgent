import { Buffer } from "node:buffer";
import { once } from "node:events";
import type { AddressInfo } from "node:net";
import { gzipSync } from "node:zlib";
import { type CredentialStore, InMemoryCredentialStore } from "@earendil-works/pi-ai";
import { describe, expect, it, vi } from "vitest";
import { WebSocketServer } from "ws";
import {
	type DoubaoAsrLimits,
	type DoubaoTtsLimits,
	type DoubaoWebSocketConnection,
	type DoubaoWebSocketConnectRequest,
	createDoubaoRealtimeSpeechRecognizer,
	createDoubaoRealtimeSpeechSynthesizer,
	createRealtimeSpeechMediaProvider,
} from "../src/index.js";

const INPUT_FORMAT = Object.freeze({
	mediaType: "audio/pcm;format=s16le",
	sampleRateHz: 16_000,
	channelCount: 1,
});

const LIMITS: DoubaoAsrLimits = Object.freeze({
	maxEndpointBytes: 256,
	maxCredentialReferenceBytes: 64,
	maxCredentialBytes: 128,
	maxAppKeyBytes: 128,
	maxResourceIdBytes: 128,
	maxIdentifierBytes: 128,
	maxMediaTypeBytes: 64,
	maxFrameBytes: 16_384,
	maxPayloadBytes: 12_288,
	maxJsonDepth: 8,
	maxPendingFrames: 8,
	maxWebSocketFragments: 16,
	maxWebSocketBufferedChunks: 32,
	maxBufferedAmountBytes: 32_768,
	connectTimeoutMs: 1_000,
	messageTimeoutMs: 1_000,
	operationTimeoutMs: 5_000,
	closeTimeoutMs: 1_000,
	maxInputChunks: 8,
	maxInputChunkBytes: 4_096,
	maxInputAudioBytes: 16_384,
	maxRecognitionEvents: 8,
	maxRecognitionTextBytes: 4_096,
});

const OUTPUT_FORMAT = Object.freeze({ mediaType: "audio/pcm;format=s16le", sampleRateHz: 24_000, channelCount: 1 });

const TTS_LIMITS: DoubaoTtsLimits = Object.freeze({
	maxEndpointBytes: 256,
	maxCredentialReferenceBytes: 64,
	maxCredentialBytes: 128,
	maxAppKeyBytes: 128,
	maxResourceIdBytes: 128,
	maxIdentifierBytes: 128,
	maxMediaTypeBytes: 64,
	maxFrameBytes: 16_384,
	maxPayloadBytes: 12_288,
	maxJsonDepth: 8,
	maxPendingFrames: 8,
	maxWebSocketFragments: 16,
	maxWebSocketBufferedChunks: 32,
	maxBufferedAmountBytes: 32_768,
	connectTimeoutMs: 1_000,
	messageTimeoutMs: 1_000,
	operationTimeoutMs: 5_000,
	closeTimeoutMs: 1_000,
	maxTextSegments: 8,
	maxTextSegmentBytes: 1_024,
	maxTextBytes: 4_096,
	maxOutputChunks: 8,
	maxOutputChunkBytes: 4_096,
	maxOutputAudioBytes: 16_384,
});

describe("Doubao realtime speech adapters", () => {
	it("streams ASR partial and final results through the provider-neutral recognizer seam", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const sent: Uint8Array[] = [];
		const inbound = [
			asrResponse(1, { result: [{ text: "江湖" }] }),
			asrResponse(-2, { result: [{ text: "演示游戏" }] }, true),
		];
		let connectionRequest: Readonly<DoubaoWebSocketConnectRequest> | undefined;
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async (frame) => {
				sent.push(Uint8Array.from(frame));
			},
			receive: async () => {
				const frame = inbound.shift();
				if (!frame) return { type: "closed", code: 1000 };
				return { type: "binary", bytes: frame };
			},
			close: vi.fn(async () => undefined),
			abort: vi.fn(() => undefined),
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "legacy", appKey: "game-app" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: LIMITS,
			connect: async (request) => {
				connectionRequest = request;
				return connection;
			},
		});
		const controller = new AbortController();

		const events = await collect(
			recognizer.recognize({
				format: INPUT_FORMAT,
				audio: chunks([1, 2], [3, 4]),
				signal: controller.signal,
			}),
		);

		expect(events).toEqual([
			{ type: "transcript.delta", text: "江湖" },
			{ type: "transcript.final", text: "演示游戏" },
		]);
		expect(connectionRequest).toMatchObject({
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			headers: {
				"X-Api-Access-Key": "doubao-secret",
				"X-Api-App-Key": "game-app",
				"X-Api-Resource-Id": "volc.bigasr.sauc.duration",
			},
			maxFrameBytes: LIMITS.maxFrameBytes,
			maxPendingFrames: LIMITS.maxPendingFrames,
			connectTimeoutMs: LIMITS.connectTimeoutMs,
		});
		expect(connectionRequest?.headers["X-Api-Request-Id"]).toMatch(
			/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u,
		);
		expect(sent).toHaveLength(3);
		expect(sent.map((frame) => (frame[1] as number) >> 4)).toEqual([1, 2, 2]);
		expect(sent.map((frame) => (frame[1] as number) & 0x0f)).toEqual([1, 1, 3]);
		expect(connection.close).toHaveBeenCalledOnce();
		expect(connection.abort).not.toHaveBeenCalled();
	});

	it("emits an ASR partial while later audio is still being produced", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const releaseAudio = Promise.withResolvers<void>();
		const receive = vi
			.fn<DoubaoWebSocketConnection["receive"]>()
			.mockResolvedValueOnce({ type: "binary", bytes: asrResponse(1, { result: [{ text: "江" }] }) })
			.mockResolvedValueOnce({
				type: "binary",
				bytes: asrResponse(-2, { result: [{ text: "江湖" }] }, true),
			});
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive,
			close: async () => undefined,
			abort: () => undefined,
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: LIMITS,
			connect: async () => connection,
		});
		async function* delayedAudio() {
			yield { bytes: Uint8Array.from([1, 2]) };
			await releaseAudio.promise;
			yield { bytes: Uint8Array.from([3, 4]) };
		}
		const iterator = recognizer
			.recognize({ format: INPUT_FORMAT, audio: delayedAudio(), signal: new AbortController().signal })
			[Symbol.asyncIterator]();
		const first = iterator.next();

		await vi.waitFor(() => expect(receive).toHaveBeenCalledOnce());
		await expect(first).resolves.toEqual({ done: false, value: { type: "transcript.delta", text: "江" } });

		releaseAudio.resolve();
		await expect(iterator.next()).resolves.toEqual({
			done: false,
			value: { type: "transcript.final", text: "江湖" },
		});
		await expect(iterator.next()).resolves.toEqual({ done: true, value: undefined });
	});

	it("streams TTS text through the official event sequence and yields audio responses", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const sessionId = "session-test";
		const sent: Uint8Array[] = [];
		const inbound = [
			ttsResponse(9, 50, "", {}, "connection-test"),
			ttsResponse(9, 150, sessionId, {}),
			ttsResponse(11, 352, sessionId, Uint8Array.from([1, 2, 3])),
			ttsResponse(11, 352, sessionId, Uint8Array.from([4, 5])),
			ttsResponse(9, 152, sessionId, {}),
			ttsResponse(9, 52, "", {}, "connection-test"),
		];
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async (frame) => {
				sent.push(Uint8Array.from(frame));
			},
			receive: async () => {
				const frame = inbound.shift();
				return frame ? { type: "binary", bytes: frame } : { type: "closed", code: 1000 };
			},
			close: vi.fn(async () => undefined),
			abort: vi.fn(() => undefined),
		};
		let connectionRequest: Readonly<DoubaoWebSocketConnectRequest> | undefined;
		const synthesizer = createDoubaoRealtimeSpeechSynthesizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/tts/bidirection",
			authentication: { type: "api-key" },
			resourceId: "seed-tts-2.0",
			outputFormat: OUTPUT_FORMAT,
			limits: TTS_LIMITS,
			createSessionId: () => sessionId,
			connect: async (request) => {
				connectionRequest = request;
				return connection;
			},
		});

		const audio = await collect(
			synthesizer.synthesize({
				voiceProfileId: "zh_female_voice",
				format: OUTPUT_FORMAT,
				text: strings("江湖", "有灵"),
				signal: new AbortController().signal,
			}),
		);

		expect(audio.map((chunk) => [...chunk.bytes])).toEqual([
			[1, 2, 3],
			[4, 5],
		]);
		expect(sent.map(readTtsEvent)).toEqual([1, 100, 200, 200, 102, 2]);
		expect(sent.map(readTtsJsonPayload)).toEqual([
			{},
			{
				req_params: {
					audio_params: { format: "pcm", sample_rate: 24_000 },
					speaker: "zh_female_voice",
				},
			},
			{ text: "江湖" },
			{ text: "有灵" },
			{},
			{},
		]);
		expect(connectionRequest).toMatchObject({
			headers: {
				"X-Api-Key": "doubao-secret",
				"X-Api-Resource-Id": "seed-tts-2.0",
			},
		});
		expect(connection.close).toHaveBeenCalledOnce();
		expect(connection.abort).not.toHaveBeenCalled();
	});

	it("composes the official Doubao synthesizer with the durable speech media provider", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const sessionId = "session-media-provider";
		const inbound = [
			ttsResponse(9, 50, "", {}, "connection-test"),
			ttsResponse(9, 150, sessionId, {}),
			ttsResponse(11, 352, sessionId, Uint8Array.of(1, 2)),
			ttsResponse(11, 352, sessionId, Uint8Array.of(3, 4)),
			ttsResponse(9, 152, sessionId, {}),
			ttsResponse(9, 52, "", {}, "connection-test"),
		];
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => {
				const frame = inbound.shift();
				return frame ? { type: "binary", bytes: frame } : closedMessage();
			},
			close: async () => undefined,
			abort: () => undefined,
		};
		const synthesizer = createDoubaoRealtimeSpeechSynthesizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/tts/bidirection",
			authentication: { type: "api-key" },
			resourceId: "seed-tts-2.0",
			outputFormat: OUTPUT_FORMAT,
			limits: TTS_LIMITS,
			createSessionId: () => sessionId,
			connect: async () => connection,
		});
		const provider = createRealtimeSpeechMediaProvider({
			synthesizer,
			voiceProfileId: "zh_female_voice",
			outputFormat: OUTPUT_FORMAT,
			limits: {
				maxIdentifierBytes: 128,
				maxTextBytes: 1_024,
				maxOutputChunks: 8,
				maxOutputChunkBytes: 4_096,
				maxOutputAudioBytes: 16_384,
			},
		});

		const observation = await provider.start(
			{ kind: "audio", input: { text: "你好" } },
			{
				jobId: "voice-job",
				attemptId: "voice-attempt",
				submissionKey: "voice-submission",
				signal: new AbortController().signal,
			},
		);

		expect(observation.status).toBe("succeeded");
		if (observation.status !== "succeeded") throw new Error("Expected synthesized WAV output.");
		const resource = observation.resources[0];
		expect(resource).toMatchObject({ kind: "audio", mediaType: "audio/wav", declaredBytes: 48 });
		if (!resource) throw new Error("Expected synthesized WAV output.");
		expect(
			Buffer.from(resource.bytes as Uint8Array)
				.subarray(0, 4)
				.toString("ascii"),
		).toBe("RIFF");
		expect([...(resource.bytes as Uint8Array).subarray(44)]).toEqual([1, 2, 3, 4]);
	});

	it("preserves cancellation through the official Doubao durable speech composition", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const sessionId = "session-media-cancel";
		const receive = vi
			.fn<DoubaoWebSocketConnection["receive"]>()
			.mockResolvedValueOnce({ type: "binary", bytes: ttsResponse(9, 50, "", {}, "connection-test") })
			.mockResolvedValueOnce({ type: "binary", bytes: ttsResponse(9, 150, sessionId, {}) })
			.mockImplementation(() => new Promise(() => undefined));
		const abort = vi.fn();
		const synthesizer = createDoubaoRealtimeSpeechSynthesizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/tts/bidirection",
			authentication: { type: "api-key" },
			resourceId: "seed-tts-2.0",
			outputFormat: OUTPUT_FORMAT,
			limits: TTS_LIMITS,
			createSessionId: () => sessionId,
			connect: async () => ({
				bufferedAmount: () => 0,
				send: async () => undefined,
				receive,
				close: async () => undefined,
				abort,
			}),
		});
		const provider = createRealtimeSpeechMediaProvider({
			synthesizer,
			voiceProfileId: "zh_female_voice",
			outputFormat: OUTPUT_FORMAT,
			limits: {
				maxIdentifierBytes: 128,
				maxTextBytes: 1_024,
				maxOutputChunks: 8,
				maxOutputChunkBytes: 4_096,
				maxOutputAudioBytes: 16_384,
			},
		});
		const controller = new AbortController();
		const reason = new Error("manual synthesis cancelled");
		const operation = provider.start(
			{ kind: "audio", input: { text: "你好" } },
			{
				jobId: "voice-job-cancel",
				attemptId: "voice-attempt-cancel",
				submissionKey: "voice-submission-cancel",
				signal: controller.signal,
			},
		);
		await vi.waitFor(() => expect(receive).toHaveBeenCalledTimes(3));

		controller.abort(reason);

		await expect(operation).rejects.toBe(reason);
		expect(abort).toHaveBeenCalledOnce();
	});

	it("classifies a TTS provider close before the start event as transport", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const abort = vi.fn();
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => closedMessage(),
			close: async () => undefined,
			abort,
		};
		const synthesizer = createDoubaoRealtimeSpeechSynthesizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/tts/bidirection",
			authentication: { type: "api-key" },
			resourceId: "seed-tts-2.0",
			outputFormat: OUTPUT_FORMAT,
			limits: TTS_LIMITS,
			createSessionId: () => "session-close",
			connect: async () => connection,
		});

		await expect(
			synthesizer
				.synthesize({
					voiceProfileId: "zh_female_wanwanxiaohe_moon_bigtts",
					format: OUTPUT_FORMAT,
					text: strings("你好"),
					signal: new AbortController().signal,
				})
				[Symbol.asyncIterator]()
				.next(),
		).rejects.toMatchObject({ code: "transport" });
		expect(abort).toHaveBeenCalledOnce();
	});

	it("yields TTS audio while later text is still being produced", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const sessionId = "session-streaming";
		const releaseText = Promise.withResolvers<void>();
		const receive = vi
			.fn<DoubaoWebSocketConnection["receive"]>()
			.mockResolvedValueOnce({ type: "binary", bytes: ttsResponse(9, 50, "", {}, "connection-test") })
			.mockResolvedValueOnce({ type: "binary", bytes: ttsResponse(9, 150, sessionId, {}) })
			.mockResolvedValueOnce({
				type: "binary",
				bytes: ttsResponse(11, 352, sessionId, Uint8Array.from([9, 8, 7])),
			})
			.mockResolvedValueOnce({ type: "binary", bytes: ttsResponse(9, 152, sessionId, {}) })
			.mockResolvedValueOnce({ type: "binary", bytes: ttsResponse(9, 52, "", {}, "connection-test") });
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive,
			close: async () => undefined,
			abort: () => undefined,
		};
		const synthesizer = createDoubaoRealtimeSpeechSynthesizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/tts/bidirection",
			authentication: { type: "api-key" },
			resourceId: "seed-tts-2.0",
			outputFormat: OUTPUT_FORMAT,
			limits: TTS_LIMITS,
			createSessionId: () => sessionId,
			connect: async () => connection,
		});
		async function* delayedText() {
			yield "先说";
			await releaseText.promise;
			yield "后说";
		}
		const iterator = synthesizer
			.synthesize({
				voiceProfileId: "zh_female_voice",
				format: OUTPUT_FORMAT,
				text: delayedText(),
				signal: new AbortController().signal,
			})
			[Symbol.asyncIterator]();
		const first = iterator.next();

		await vi.waitFor(() => expect(receive).toHaveBeenCalledTimes(3));
		await expect(first).resolves.toEqual({ done: false, value: { bytes: Uint8Array.from([9, 8, 7]) } });

		releaseText.resolve();
		await expect(iterator.next()).resolves.toEqual({ done: true, value: undefined });
	});

	it("rejects a TTS control event without the official JSON serialization marker", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const sessionId = "session-invalid-control";
		const inbound = [
			ttsResponse(9, 50, "", {}, "connection-test", 0),
			ttsResponse(9, 150, sessionId, {}),
			ttsResponse(9, 152, sessionId, {}),
			ttsResponse(9, 52, "", {}, "connection-test"),
		];
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => {
				const frame = inbound.shift();
				return frame ? { type: "binary", bytes: frame } : closedMessage();
			},
			close: async () => undefined,
			abort: () => undefined,
		};
		const synthesizer = createDoubaoRealtimeSpeechSynthesizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/tts/bidirection",
			authentication: { type: "api-key" },
			resourceId: "seed-tts-2.0",
			outputFormat: OUTPUT_FORMAT,
			limits: TTS_LIMITS,
			createSessionId: () => sessionId,
			connect: async () => connection,
		});

		await expect(
			collect(
				synthesizer.synthesize({
					voiceProfileId: "zh_female_voice",
					format: OUTPUT_FORMAT,
					text: strings("台词"),
					signal: new AbortController().signal,
				}),
			),
		).rejects.toMatchObject({ code: "protocol" });
	});

	it("rejects invalid UTF-8 in a TTS control response", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const sessionId = "session-invalid-utf8";
		const malformedJson = Buffer.concat([Buffer.from('{"message":"'), Buffer.from([0xff]), Buffer.from('"}')]);
		const inbound = [
			ttsResponse(9, 50, "", malformedJson, "connection-test"),
			ttsResponse(9, 150, sessionId, {}),
			ttsResponse(9, 152, sessionId, {}),
			ttsResponse(9, 52, "", {}, "connection-test"),
		];
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => ({ type: "binary", bytes: inbound.shift() ?? new Uint8Array() }),
			close: async () => undefined,
			abort: () => undefined,
		};
		const synthesizer = createDoubaoRealtimeSpeechSynthesizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/tts/bidirection",
			authentication: { type: "api-key" },
			resourceId: "seed-tts-2.0",
			outputFormat: OUTPUT_FORMAT,
			limits: TTS_LIMITS,
			createSessionId: () => sessionId,
			connect: async () => connection,
		});

		await expect(
			collect(
				synthesizer.synthesize({
					voiceProfileId: "zh_female_voice",
					format: OUTPUT_FORMAT,
					text: strings("你好"),
					signal: new AbortController().signal,
				}),
			),
		).rejects.toMatchObject({ code: "protocol" });
	});

	it("rejects an oversized TTS audio chunk before copying its payload", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const sessionId = "session-output-capacity";
		const inbound = [
			ttsResponse(9, 50, "", {}, "connection-test"),
			ttsResponse(9, 150, sessionId, {}),
			ttsResponse(11, 352, sessionId, Uint8Array.of(1, 2)),
		];
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => ({ type: "binary", bytes: inbound.shift() ?? new Uint8Array() }),
			close: async () => undefined,
			abort: () => undefined,
		};
		const synthesizer = createDoubaoRealtimeSpeechSynthesizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/tts/bidirection",
			authentication: { type: "api-key" },
			resourceId: "seed-tts-2.0",
			outputFormat: OUTPUT_FORMAT,
			limits: { ...TTS_LIMITS, maxOutputChunkBytes: 1 },
			createSessionId: () => sessionId,
			connect: async () => connection,
		});
		const copyPayload = vi.spyOn(Uint8Array, "from").mockImplementation(() => {
			throw new Error("payload copied before capacity admission");
		});

		try {
			await expect(
				synthesizer
					.synthesize({
						voiceProfileId: "zh_female_voice",
						format: OUTPUT_FORMAT,
						text: strings("你好"),
						signal: new AbortController().signal,
					})
					[Symbol.asyncIterator]()
					.next(),
			).rejects.toMatchObject({ code: "capacity" });
			expect(copyPayload).not.toHaveBeenCalled();
		} finally {
			copyPayload.mockRestore();
		}
	});

	it.each([
		["chunk count", { maxOutputChunks: 1 }],
		["aggregate bytes", { maxOutputAudioBytes: 2 }],
	] as const)("rejects TTS audio beyond the %s budget before copying another payload", async (_label, limit) => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const sessionId = "session-output-total";
		const inbound = [
			ttsResponse(9, 50, "", {}, "connection-test"),
			ttsResponse(9, 150, sessionId, {}),
			ttsResponse(11, 352, sessionId, Uint8Array.of(1)),
			ttsResponse(11, 352, sessionId, Uint8Array.of(2, 3)),
		];
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => ({ type: "binary", bytes: inbound.shift() ?? new Uint8Array() }),
			close: async () => undefined,
			abort: () => undefined,
		};
		const synthesizer = createDoubaoRealtimeSpeechSynthesizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/tts/bidirection",
			authentication: { type: "api-key" },
			resourceId: "seed-tts-2.0",
			outputFormat: OUTPUT_FORMAT,
			limits: { ...TTS_LIMITS, ...limit },
			createSessionId: () => sessionId,
			connect: async () => connection,
		});
		const copyPayload = vi.spyOn(Uint8Array, "from");

		try {
			const iterator = synthesizer
				.synthesize({
					voiceProfileId: "zh_female_voice",
					format: OUTPUT_FORMAT,
					text: strings("你好"),
					signal: new AbortController().signal,
				})
				[Symbol.asyncIterator]();
			await expect(iterator.next()).resolves.toEqual({ done: false, value: { bytes: Uint8Array.of(1) } });
			expect(copyPayload).toHaveBeenCalledOnce();
			await expect(iterator.next()).rejects.toMatchObject({ code: "capacity" });
			expect(copyPayload).toHaveBeenCalledOnce();
		} finally {
			copyPayload.mockRestore();
		}
	});

	it("rejects a TTS control event beyond the host JSON depth budget", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const sessionId = "session-deep-control";
		const inbound = [
			ttsResponse(9, 50, "", { nested: { deeper: { value: 1 } } }, "connection-test"),
			ttsResponse(9, 150, sessionId, {}),
			ttsResponse(9, 152, sessionId, {}),
			ttsResponse(9, 52, "", {}, "connection-test"),
		];
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => {
				const frame = inbound.shift();
				return frame ? { type: "binary", bytes: frame } : closedMessage();
			},
			close: async () => undefined,
			abort: () => undefined,
		};
		const synthesizer = createDoubaoRealtimeSpeechSynthesizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/tts/bidirection",
			authentication: { type: "api-key" },
			resourceId: "seed-tts-2.0",
			outputFormat: OUTPUT_FORMAT,
			limits: { ...TTS_LIMITS, maxJsonDepth: 2 },
			createSessionId: () => sessionId,
			connect: async () => connection,
		});

		await expect(
			collect(
				synthesizer.synthesize({
					voiceProfileId: "zh_female_voice",
					format: OUTPUT_FORMAT,
					text: strings("台词"),
					signal: new AbortController().signal,
				}),
			),
		).rejects.toMatchObject({ code: "protocol" });
	});

	it("rejects a TTS session control event carried as binary audio", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const sessionId = "session-wrong-message-type";
		const inbound = [
			ttsResponse(9, 50, "", {}, "connection-test"),
			ttsResponse(9, 150, sessionId, {}),
			ttsResponse(11, 152, sessionId, Uint8Array.from([1])),
			ttsResponse(9, 52, "", {}, "connection-test"),
		];
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => {
				const frame = inbound.shift();
				return frame ? { type: "binary", bytes: frame } : closedMessage();
			},
			close: async () => undefined,
			abort: () => undefined,
		};
		const synthesizer = createDoubaoRealtimeSpeechSynthesizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/tts/bidirection",
			authentication: { type: "api-key" },
			resourceId: "seed-tts-2.0",
			outputFormat: OUTPUT_FORMAT,
			limits: TTS_LIMITS,
			createSessionId: () => sessionId,
			connect: async () => connection,
		});

		await expect(
			collect(
				synthesizer.synthesize({
					voiceProfileId: "zh_female_voice",
					format: OUTPUT_FORMAT,
					text: strings("台词"),
					signal: new AbortController().signal,
				}),
			),
		).rejects.toMatchObject({ code: "protocol" });
	});

	it("uses the bounded default WebSocket transport and accepts a fragmented ASR response", async () => {
		const server = new WebSocketServer({ host: "127.0.0.1", port: 0, maxPayload: LIMITS.maxFrameBytes });
		await once(server, "listening");
		const address = server.address() as AddressInfo;
		const headers = Promise.withResolvers<Readonly<Record<string, string | string[] | undefined>>>();
		server.on("connection", (socket, request) => {
			headers.resolve(request.headers);
			let received = 0;
			socket.on("message", (_data, isBinary) => {
				expect(isBinary).toBe(true);
				received += 1;
				if (received !== 3) return;
				socket.send(asrResponse(1, { result: [{ text: "分" }] }));
				const final = asrResponse(-2, { result: [{ text: "分片" }] }, true);
				socket.send(final.subarray(0, 7), { binary: true, fin: false });
				socket.send(final.subarray(7), { binary: true, fin: true });
			});
		});
		try {
			const credentials = new InMemoryCredentialStore();
			await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
			const options = {
				credentials,
				credentialReference: "doubao-account",
				endpoint: `ws://127.0.0.1:${address.port}/api/v3/sauc/bigmodel_async`,
				authentication: { type: "api-key" as const },
				resourceId: "volc.bigasr.sauc.duration",
				userId: "game-runtime",
				modelName: "bigmodel",
				language: "zh-CN",
				inputFormat: INPUT_FORMAT,
				limits: LIMITS,
			} as unknown as Parameters<typeof createDoubaoRealtimeSpeechRecognizer>[0];
			const recognizer = createDoubaoRealtimeSpeechRecognizer(options);

			await expect(
				collect(
					recognizer.recognize({
						format: INPUT_FORMAT,
						audio: chunks([1, 2], [3, 4]),
						signal: new AbortController().signal,
					}),
				),
			).resolves.toEqual([
				{ type: "transcript.delta", text: "分" },
				{ type: "transcript.final", text: "分片" },
			]);
			await expect(headers.promise).resolves.toMatchObject({
				"x-api-key": "doubao-secret",
				"x-api-resource-id": "volc.bigasr.sauc.duration",
			});
		} finally {
			for (const client of server.clients) client.terminate();
			await new Promise<void>((resolve) => server.close(() => resolve()));
		}
	});

	it("preserves caller cancellation and aborts a non-cooperative ASR connection", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const receive = vi.fn(() => new Promise<Readonly<ReturnType<typeof closedMessage>>>(() => undefined));
		const abort = vi.fn();
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive,
			close: async () => undefined,
			abort,
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: LIMITS,
			connect: async () => connection,
		});
		const controller = new AbortController();
		const reason = new Error("caller stopped this exact recognition");
		const pending = recognizer
			.recognize({ format: INPUT_FORMAT, audio: chunks([1, 2]), signal: controller.signal })
			[Symbol.asyncIterator]()
			.next();
		await vi.waitFor(() => expect(receive).toHaveBeenCalledOnce());

		controller.abort(reason);

		await expect(pending).rejects.toBe(reason);
		expect(abort).toHaveBeenCalledOnce();
	});

	it("times out a silent ASR provider at the host-supplied message deadline", async () => {
		vi.useFakeTimers();
		try {
			const credentials = new InMemoryCredentialStore();
			await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
			const receive = vi.fn(() => new Promise<Readonly<ReturnType<typeof closedMessage>>>(() => undefined));
			const abort = vi.fn();
			const connection: DoubaoWebSocketConnection = {
				bufferedAmount: () => 0,
				send: async () => undefined,
				receive,
				close: async () => undefined,
				abort,
			};
			const recognizer = createDoubaoRealtimeSpeechRecognizer({
				credentials,
				credentialReference: "doubao-account",
				endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
				authentication: { type: "api-key" },
				resourceId: "volc.bigasr.sauc.duration",
				userId: "game-runtime",
				modelName: "bigmodel",
				language: "zh-CN",
				inputFormat: INPUT_FORMAT,
				limits: { ...LIMITS, messageTimeoutMs: 10, operationTimeoutMs: 100 },
				connect: async () => connection,
			});
			const pending = recognizer
				.recognize({ format: INPUT_FORMAT, audio: chunks([1, 2]), signal: new AbortController().signal })
				[Symbol.asyncIterator]()
				.next();
			await vi.advanceTimersByTimeAsync(0);
			expect(receive).toHaveBeenCalledOnce();
			const rejection = expect(pending).rejects.toMatchObject({
				code: "timeout",
			} satisfies Partial<Error & { code: string }>);

			await vi.advanceTimersByTimeAsync(10);

			await rejection;
			expect(abort).toHaveBeenCalledOnce();
		} finally {
			vi.useRealTimers();
		}
	});

	it("bounds a non-cooperative custom connector with the host connect deadline", async () => {
		vi.useFakeTimers();
		try {
			const credentials = new InMemoryCredentialStore();
			await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
			const connect = vi.fn(() => new Promise<DoubaoWebSocketConnection>(() => undefined));
			const recognizer = createDoubaoRealtimeSpeechRecognizer({
				credentials,
				credentialReference: "doubao-account",
				endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
				authentication: { type: "api-key" },
				resourceId: "volc.bigasr.sauc.duration",
				userId: "game-runtime",
				modelName: "bigmodel",
				language: "zh-CN",
				inputFormat: INPUT_FORMAT,
				limits: { ...LIMITS, connectTimeoutMs: 10, operationTimeoutMs: 100 },
				connect,
			});
			const pending = recognizer
				.recognize({ format: INPUT_FORMAT, audio: chunks([1, 2]), signal: new AbortController().signal })
				[Symbol.asyncIterator]()
				.next();
			await vi.advanceTimersByTimeAsync(0);
			expect(connect).toHaveBeenCalledOnce();
			const rejection = expect(pending).rejects.toMatchObject({ code: "timeout" });

			await vi.advanceTimersByTimeAsync(10);

			await rejection;
		} finally {
			vi.useRealTimers();
		}
	});

	it("aborts a custom connection that succeeds after its connect deadline", async () => {
		vi.useFakeTimers();
		try {
			const credentials = new InMemoryCredentialStore();
			await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
			const late = Promise.withResolvers<DoubaoWebSocketConnection>();
			const abort = vi.fn();
			const connection: DoubaoWebSocketConnection = {
				bufferedAmount: () => 0,
				send: async () => undefined,
				receive: async () => closedMessage(),
				close: async () => undefined,
				abort,
			};
			let connectSignal: AbortSignal | undefined;
			const recognizer = createDoubaoRealtimeSpeechRecognizer({
				credentials,
				credentialReference: "doubao-account",
				endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
				authentication: { type: "api-key" },
				resourceId: "volc.bigasr.sauc.duration",
				userId: "game-runtime",
				modelName: "bigmodel",
				language: "zh-CN",
				inputFormat: INPUT_FORMAT,
				limits: { ...LIMITS, connectTimeoutMs: 10, operationTimeoutMs: 100 },
				connect: async (_request, signal) => {
					connectSignal = signal;
					return late.promise;
				},
			});
			const pending = recognizer
				.recognize({ format: INPUT_FORMAT, audio: chunks([1, 2]), signal: new AbortController().signal })
				[Symbol.asyncIterator]()
				.next();
			await vi.advanceTimersByTimeAsync(0);
			const rejection = expect(pending).rejects.toMatchObject({ code: "timeout" });

			await vi.advanceTimersByTimeAsync(10);
			await rejection;
			expect(connectSignal?.aborted).toBe(true);

			late.resolve(connection);
			await vi.waitFor(() => expect(abort).toHaveBeenCalledOnce());
		} finally {
			vi.useRealTimers();
		}
	});

	it("surfaces ASR send backpressure immediately instead of waiting for a response timeout", async () => {
		vi.useFakeTimers();
		try {
			const credentials = new InMemoryCredentialStore();
			await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
			const bufferedAmount = vi
				.fn<DoubaoWebSocketConnection["bufferedAmount"]>()
				.mockReturnValueOnce(0)
				.mockReturnValue(LIMITS.maxBufferedAmountBytes + 1);
			const abort = vi.fn();
			const connection: DoubaoWebSocketConnection = {
				bufferedAmount,
				send: async () => undefined,
				receive: () => new Promise<Readonly<ReturnType<typeof closedMessage>>>(() => undefined),
				close: async () => undefined,
				abort,
			};
			const recognizer = createDoubaoRealtimeSpeechRecognizer({
				credentials,
				credentialReference: "doubao-account",
				endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
				authentication: { type: "api-key" },
				resourceId: "volc.bigasr.sauc.duration",
				userId: "game-runtime",
				modelName: "bigmodel",
				language: "zh-CN",
				inputFormat: INPUT_FORMAT,
				limits: { ...LIMITS, messageTimeoutMs: 10 },
				connect: async () => connection,
			});
			const pending = recognizer
				.recognize({ format: INPUT_FORMAT, audio: chunks([1, 2]), signal: new AbortController().signal })
				[Symbol.asyncIterator]()
				.next();
			const rejection = expect(pending).rejects.toMatchObject({ code: "capacity" });

			await vi.advanceTimersByTimeAsync(10);

			await rejection;
			expect(abort).toHaveBeenCalledOnce();
		} finally {
			vi.useRealTimers();
		}
	});

	it("rejects an out-of-order final ASR frame as late protocol data", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const inbound = [
			asrResponse(2, { result: [{ text: "新结果" }] }),
			asrResponse(-1, { result: [{ text: "旧结果" }] }, true),
		];
		const abort = vi.fn();
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => {
				const frame = inbound.shift();
				return frame ? { type: "binary", bytes: frame } : closedMessage();
			},
			close: async () => undefined,
			abort,
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: LIMITS,
			connect: async () => connection,
		});
		const iterator = recognizer
			.recognize({ format: INPUT_FORMAT, audio: chunks([1, 2]), signal: new AbortController().signal })
			[Symbol.asyncIterator]();

		await expect(iterator.next()).resolves.toEqual({
			done: false,
			value: { type: "transcript.delta", text: "新结果" },
		});
		await expect(iterator.next()).rejects.toMatchObject({ code: "protocol" });
		expect(abort).toHaveBeenCalledOnce();
	});

	it("classifies an oversized ASR frame as capacity with a redacted error", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => ({
				type: "binary",
				bytes: new Uint8Array(LIMITS.maxFrameBytes + 1).fill(0x73),
			}),
			close: async () => undefined,
			abort: () => undefined,
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: LIMITS,
			connect: async () => connection,
		});

		const failure = await recognizer
			.recognize({ format: INPUT_FORMAT, audio: chunks([1, 2]), signal: new AbortController().signal })
			[Symbol.asyncIterator]()
			.next()
			.catch((error: unknown) => error);

		expect(failure).toMatchObject({ code: "capacity" });
		expect(String(failure)).not.toContain("ssss");
	});

	it("rejects an infeasible ASR configuration budget before opening a connection", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => closedMessage(),
			close: async () => undefined,
			abort: () => undefined,
		};
		const connect = vi.fn(async () => connection);
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: { ...LIMITS, maxPayloadBytes: 1, maxFrameBytes: 13 },
			connect,
		});

		await expect(
			recognizer
				.recognize({ format: INPUT_FORMAT, audio: chunks([1, 2]), signal: new AbortController().signal })
				[Symbol.asyncIterator]()
				.next(),
		).rejects.toMatchObject({ code: "capacity" });
		expect(connect).not.toHaveBeenCalled();
	});

	it("classifies an ASR provider close before the final transcript as transport", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const abort = vi.fn();
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => closedMessage(),
			close: async () => undefined,
			abort,
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: LIMITS,
			connect: async () => connection,
		});

		await expect(
			recognizer
				.recognize({ format: INPUT_FORMAT, audio: chunks([1, 2]), signal: new AbortController().signal })
				[Symbol.asyncIterator]()
				.next(),
		).rejects.toMatchObject({ code: "transport" });
		expect(abort).toHaveBeenCalledOnce();
	});

	it("rejects a non-Uint8Array audio chunk before consuming its iterable", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const iterate = vi.fn(function* () {
			yield 1;
		});
		const audio = chunksWithValue({
			bytes: { [Symbol.iterator]: iterate } as unknown as Uint8Array,
		});
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => ({
				type: "binary",
				bytes: asrResponse(-1, { result: [{ text: "完成" }] }, true),
			}),
			close: async () => undefined,
			abort: () => undefined,
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: LIMITS,
			connect: async () => connection,
		});

		await expect(
			recognizer
				.recognize({ format: INPUT_FORMAT, audio, signal: new AbortController().signal })
				[Symbol.asyncIterator]()
				.next(),
		).rejects.toMatchObject({ code: "protocol" });
		expect(iterate).not.toHaveBeenCalled();
	});

	it("copies a Uint8Array subclass without consuming its overridden iterator", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const consumeIterator = vi.fn();
		class StatefulAudioChunk extends Uint8Array {
			override [Symbol.iterator](): ArrayIterator<number> {
				consumeIterator();
				return super[Symbol.iterator]();
			}
		}
		const audio = chunksWithValue({ bytes: new StatefulAudioChunk([1, 2]) });
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => ({
				type: "binary",
				bytes: asrResponse(-1, { result: [{ text: "完成" }] }, true),
			}),
			close: async () => undefined,
			abort: () => undefined,
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: LIMITS,
			connect: async () => connection,
		});

		await expect(
			collect(recognizer.recognize({ format: INPUT_FORMAT, audio, signal: new AbortController().signal })),
		).resolves.toEqual([{ type: "transcript.final", text: "完成" }]);
		expect(consumeIterator).not.toHaveBeenCalled();
	});

	it("copies audio by its intrinsic typed-array length instead of an overridden byteLength", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const readByteLength = vi.fn();
		const bytes = Uint8Array.from([1, 2]);
		Object.defineProperty(bytes, "byteLength", {
			get: () => {
				readByteLength();
				return readByteLength.mock.calls.length < 3 ? 2 : LIMITS.maxInputChunkBytes + 1;
			},
		});
		const audio = chunksWithValue({ bytes });
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => ({
				type: "binary",
				bytes: asrResponse(-1, { result: [{ text: "完成" }] }, true),
			}),
			close: async () => undefined,
			abort: () => undefined,
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: LIMITS,
			connect: async () => connection,
		});

		await expect(
			collect(recognizer.recognize({ format: INPUT_FORMAT, audio, signal: new AbortController().signal })),
		).resolves.toEqual([{ type: "transcript.final", text: "完成" }]);
		expect(readByteLength).not.toHaveBeenCalled();
	});

	it.each([
		["chunk count", { maxInputChunks: 1 }],
		["aggregate bytes", { maxInputAudioBytes: 2 }],
	] as const)("rejects ASR audio beyond the %s budget before copying another chunk", async (_label, limit) => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		let copiedAfterCapacity = 0;
		let restoreCopy: (() => void) | undefined;
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async (frame) => {
				const messageType = frame[1] === undefined ? undefined : frame[1] >> 4;
				if (messageType === 2 && restoreCopy === undefined) {
					const copy = vi.spyOn(Uint8Array.prototype, "set").mockImplementation(() => {
						copiedAfterCapacity += 1;
						throw new Error("audio copied before capacity admission");
					});
					restoreCopy = () => copy.mockRestore();
				}
			},
			receive: () => new Promise(() => undefined),
			close: async () => undefined,
			abort: () => undefined,
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: { ...LIMITS, ...limit },
			connect: async () => connection,
		});

		try {
			await expect(
				recognizer
					.recognize({
						format: INPUT_FORMAT,
						audio: chunks([1], [2, 3]),
						signal: new AbortController().signal,
					})
					[Symbol.asyncIterator]()
					.next(),
			).rejects.toMatchObject({ code: "capacity" });
			expect(restoreCopy).toBeDefined();
			expect(copiedAfterCapacity).toBe(0);
		} finally {
			restoreCopy?.();
		}
	});

	it("rejects highly compressed ASR output at the decompression budget", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const compressed = asrResponse(-1, { result: [{ text: "灵".repeat(4_096) }] }, true);
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => ({ type: "binary", bytes: compressed }),
			close: async () => undefined,
			abort: () => undefined,
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: { ...LIMITS, maxPayloadBytes: 128 },
			connect: async () => connection,
		});

		await expect(
			recognizer
				.recognize({ format: INPUT_FORMAT, audio: chunks([1, 2]), signal: new AbortController().signal })
				[Symbol.asyncIterator]()
				.next(),
		).rejects.toMatchObject({ code: "capacity" });
	});

	it("classifies an unsupported ASR serialization marker as protocol", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const malformed = asrResponse(-1, { result: [{ text: "完成" }] }, true);
		malformed[2] = 0x01;
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => ({ type: "binary", bytes: malformed }),
			close: async () => undefined,
			abort: () => undefined,
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: LIMITS,
			connect: async () => connection,
		});

		await expect(
			recognizer
				.recognize({ format: INPUT_FORMAT, audio: chunks([1, 2]), signal: new AbortController().signal })
				[Symbol.asyncIterator]()
				.next(),
		).rejects.toMatchObject({ code: "protocol" });
	});

	it("rejects invalid UTF-8 in an ASR JSON response", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const malformed = asrResponseBytes(
			-1,
			Buffer.concat([Buffer.from('{"result":[{"text":"'), Buffer.from([0xff]), Buffer.from('"}]}')]),
			true,
		);
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => ({ type: "binary", bytes: malformed }),
			close: async () => undefined,
			abort: () => undefined,
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: LIMITS,
			connect: async () => connection,
		});

		await expect(
			recognizer
				.recognize({ format: INPUT_FORMAT, audio: chunks([1, 2]), signal: new AbortController().signal })
				[Symbol.asyncIterator]()
				.next(),
		).rejects.toMatchObject({ code: "protocol" });
	});

	it("closes a blocked ASR audio iterator when the caller cancels", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const closeAudio = vi.fn(async () => ({ done: true as const, value: undefined }));
		let reads = 0;
		const audio: AsyncIterable<Readonly<{ bytes: Uint8Array }>> = {
			[Symbol.asyncIterator]: () => ({
				next: async () => {
					reads += 1;
					if (reads === 1) return { done: false as const, value: { bytes: Uint8Array.from([1, 2]) } };
					return new Promise<IteratorResult<Readonly<{ bytes: Uint8Array }>>>(() => undefined);
				},
				return: closeAudio,
			}),
		};
		const receive = vi.fn(() => new Promise<Readonly<ReturnType<typeof closedMessage>>>(() => undefined));
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive,
			close: async () => undefined,
			abort: () => undefined,
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: LIMITS,
			connect: async () => connection,
		});
		const controller = new AbortController();
		const reason = new Error("microphone stopped");
		const pending = recognizer
			.recognize({ format: INPUT_FORMAT, audio, signal: controller.signal })
			[Symbol.asyncIterator]()
			.next();
		await vi.waitFor(() => expect(receive).toHaveBeenCalledOnce());

		controller.abort(reason);

		await expect(pending).rejects.toBe(reason);
		await vi.waitFor(() => expect(closeAudio).toHaveBeenCalledOnce());
	});

	it("stops blocked ASR audio production when the provider response is invalid", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const closeAudio = vi.fn(async () => ({ done: true as const, value: undefined }));
		let reads = 0;
		const audio: AsyncIterable<Readonly<{ bytes: Uint8Array }>> = {
			[Symbol.asyncIterator]: () => ({
				next: async () => {
					reads += 1;
					if (reads === 1) return { done: false as const, value: { bytes: Uint8Array.from([1, 2]) } };
					return new Promise<IteratorResult<Readonly<{ bytes: Uint8Array }>>>(() => undefined);
				},
				return: closeAudio,
			}),
		};
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => ({ type: "text", text: "unexpected" }),
			close: async () => undefined,
			abort: () => undefined,
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: LIMITS,
			connect: async () => connection,
		});

		await expect(
			recognizer
				.recognize({ format: INPUT_FORMAT, audio, signal: new AbortController().signal })
				[Symbol.asyncIterator]()
				.next(),
		).rejects.toMatchObject({ code: "protocol" });
		await vi.waitFor(() => expect(closeAudio).toHaveBeenCalledOnce());
	});

	it("preserves the primary ASR failure when custom connection cleanup throws", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => ({ type: "text", text: "unexpected" }),
			close: async () => undefined,
			abort: () => {
				throw new Error("internal cleanup details");
			},
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: LIMITS,
			connect: async () => connection,
		});

		const failure = await recognizer
			.recognize({ format: INPUT_FORMAT, audio: chunks([1, 2]), signal: new AbortController().signal })
			[Symbol.asyncIterator]()
			.next()
			.catch((error: unknown) => error);

		expect(failure).toMatchObject({ code: "protocol" });
		expect(String(failure)).not.toContain("internal cleanup details");
	});

	it("closes a blocked TTS text iterator when the caller cancels", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const closeText = vi.fn(async () => ({ done: true as const, value: undefined }));
		let reads = 0;
		const text: AsyncIterable<string> = {
			[Symbol.asyncIterator]: () => ({
				next: async () => {
					reads += 1;
					if (reads === 1) return { done: false as const, value: "开场" };
					return new Promise<IteratorResult<string>>(() => undefined);
				},
				return: closeText,
			}),
		};
		const sessionId = "session-cancelled";
		const receive = vi
			.fn<DoubaoWebSocketConnection["receive"]>()
			.mockResolvedValueOnce({ type: "binary", bytes: ttsResponse(9, 50, "", {}, "connection-test") })
			.mockResolvedValueOnce({ type: "binary", bytes: ttsResponse(9, 150, sessionId, {}) })
			.mockImplementation(() => new Promise(() => undefined));
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive,
			close: async () => undefined,
			abort: () => undefined,
		};
		const synthesizer = createDoubaoRealtimeSpeechSynthesizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/tts/bidirection",
			authentication: { type: "api-key" },
			resourceId: "seed-tts-2.0",
			outputFormat: OUTPUT_FORMAT,
			limits: TTS_LIMITS,
			createSessionId: () => sessionId,
			connect: async () => connection,
		});
		const controller = new AbortController();
		const reason = new Error("dialogue interrupted");
		const pending = synthesizer
			.synthesize({ voiceProfileId: "zh_female_voice", format: OUTPUT_FORMAT, text, signal: controller.signal })
			[Symbol.asyncIterator]()
			.next();
		await vi.waitFor(() => expect(receive).toHaveBeenCalledTimes(3));

		controller.abort(reason);

		await expect(pending).rejects.toBe(reason);
		await vi.waitFor(() => expect(closeText).toHaveBeenCalledOnce());
	});

	it("closes the TTS text iterator when setup fails after its first segment", async () => {
		const credentials = new InMemoryCredentialStore();
		const closeText = vi.fn(async () => ({ done: true as const, value: undefined }));
		const text: AsyncIterable<string> = {
			[Symbol.asyncIterator]: () => ({
				next: async () => ({ done: false as const, value: "已读取台词" }),
				return: closeText,
			}),
		};
		const connect = vi.fn();
		const synthesizer = createDoubaoRealtimeSpeechSynthesizer({
			credentials,
			credentialReference: "missing-doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/tts/bidirection",
			authentication: { type: "api-key" },
			resourceId: "seed-tts-2.0",
			outputFormat: OUTPUT_FORMAT,
			limits: TTS_LIMITS,
			createSessionId: () => "session-setup-failure",
			connect,
		});

		await expect(
			synthesizer
				.synthesize({
					voiceProfileId: "zh_female_voice",
					format: OUTPUT_FORMAT,
					text,
					signal: new AbortController().signal,
				})
				[Symbol.asyncIterator]()
				.next(),
		).rejects.toMatchObject({ code: "credential" });
		await vi.waitFor(() => expect(closeText).toHaveBeenCalledOnce());
		expect(connect).not.toHaveBeenCalled();
	});

	it("stops blocked TTS text production when the provider fails the session", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const closeText = vi.fn(async () => ({ done: true as const, value: undefined }));
		let reads = 0;
		const text: AsyncIterable<string> = {
			[Symbol.asyncIterator]: () => ({
				next: async () => {
					reads += 1;
					if (reads === 1) return { done: false as const, value: "未完台词" };
					return new Promise<IteratorResult<string>>(() => undefined);
				},
				return: closeText,
			}),
		};
		const sessionId = "session-failed";
		const receive = vi
			.fn<DoubaoWebSocketConnection["receive"]>()
			.mockResolvedValueOnce({ type: "binary", bytes: ttsResponse(9, 50, "", {}, "connection-test") })
			.mockResolvedValueOnce({ type: "binary", bytes: ttsResponse(9, 150, sessionId, {}) })
			.mockResolvedValueOnce({ type: "binary", bytes: ttsResponse(9, 153, sessionId, { code: 55000000 }) });
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive,
			close: async () => undefined,
			abort: () => undefined,
		};
		const synthesizer = createDoubaoRealtimeSpeechSynthesizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/tts/bidirection",
			authentication: { type: "api-key" },
			resourceId: "seed-tts-2.0",
			outputFormat: OUTPUT_FORMAT,
			limits: TTS_LIMITS,
			createSessionId: () => sessionId,
			connect: async () => connection,
		});

		await expect(
			synthesizer
				.synthesize({
					voiceProfileId: "zh_female_voice",
					format: OUTPUT_FORMAT,
					text,
					signal: new AbortController().signal,
				})
				[Symbol.asyncIterator]()
				.next(),
		).rejects.toMatchObject({ code: "transport" });
		await vi.waitFor(() => expect(closeText).toHaveBeenCalledOnce());
	});

	it("applies the whole-operation deadline while the credential backend is non-cooperative", async () => {
		vi.useFakeTimers();
		try {
			const credentials: CredentialStore = {
				read: () => new Promise(() => undefined),
				list: async () => [],
				modify: async () => undefined,
				delete: async () => undefined,
			};
			const connect = vi.fn();
			const recognizer = createDoubaoRealtimeSpeechRecognizer({
				credentials,
				credentialReference: "doubao-account",
				endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
				authentication: { type: "api-key" },
				resourceId: "volc.bigasr.sauc.duration",
				userId: "game-runtime",
				modelName: "bigmodel",
				language: "zh-CN",
				inputFormat: INPUT_FORMAT,
				limits: { ...LIMITS, operationTimeoutMs: 10 },
				connect,
			});
			const pending = recognizer
				.recognize({ format: INPUT_FORMAT, audio: chunks([1, 2]), signal: new AbortController().signal })
				[Symbol.asyncIterator]()
				.next();
			const rejection = expect(pending).rejects.toMatchObject({ code: "timeout" });

			await vi.advanceTimersByTimeAsync(10);

			await rejection;
			expect(connect).not.toHaveBeenCalled();
		} finally {
			vi.useRealTimers();
		}
	});

	it("aborts an injected connection when graceful close exceeds the host deadline", async () => {
		vi.useFakeTimers();
		try {
			const credentials = new InMemoryCredentialStore();
			await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
			const abort = vi.fn();
			const close = vi.fn(() => new Promise<void>(() => undefined));
			const connection: DoubaoWebSocketConnection = {
				bufferedAmount: () => 0,
				send: async () => undefined,
				receive: async () => ({
					type: "binary",
					bytes: asrResponse(-1, { result: [{ text: "完成" }] }, true),
				}),
				close,
				abort,
			};
			const recognizer = createDoubaoRealtimeSpeechRecognizer({
				credentials,
				credentialReference: "doubao-account",
				endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
				authentication: { type: "api-key" },
				resourceId: "volc.bigasr.sauc.duration",
				userId: "game-runtime",
				modelName: "bigmodel",
				language: "zh-CN",
				inputFormat: INPUT_FORMAT,
				limits: { ...LIMITS, closeTimeoutMs: 10 },
				connect: async () => connection,
			});
			const pending = collect(
				recognizer.recognize({
					format: INPUT_FORMAT,
					audio: chunks([1, 2]),
					signal: new AbortController().signal,
				}),
			);
			const rejection = expect(pending).rejects.toMatchObject({ code: "timeout" });
			await vi.waitFor(() => expect(close).toHaveBeenCalledOnce());

			await vi.advanceTimersByTimeAsync(10);

			await rejection;
			expect(abort).toHaveBeenCalledOnce();
		} finally {
			vi.useRealTimers();
		}
	});

	it("redacts a custom graceful-close failure after a completed ASR stream", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("doubao-account", async () => ({ type: "api_key", key: "doubao-secret" }));
		const abort = vi.fn();
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => ({
				type: "binary",
				bytes: asrResponse(-1, { result: [{ text: "完成" }] }, true),
			}),
			close: async () => {
				throw new Error("internal graceful-close details");
			},
			abort,
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: LIMITS,
			connect: async () => connection,
		});

		const failure = await collect(
			recognizer.recognize({
				format: INPUT_FORMAT,
				audio: chunks([1, 2]),
				signal: new AbortController().signal,
			}),
		).catch((error: unknown) => error);

		expect(failure).toMatchObject({ code: "transport" });
		expect(String(failure)).not.toContain("internal graceful-close details");
		expect(abort).toHaveBeenCalledOnce();
	});

	it("rejects timer limits outside the Node.js signed 32-bit range", () => {
		const credentials = new InMemoryCredentialStore();

		expect(() =>
			createDoubaoRealtimeSpeechRecognizer({
				credentials,
				credentialReference: "doubao-account",
				endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
				authentication: { type: "api-key" },
				resourceId: "volc.bigasr.sauc.duration",
				userId: "game-runtime",
				modelName: "bigmodel",
				language: "zh-CN",
				inputFormat: INPUT_FORMAT,
				limits: { ...LIMITS, operationTimeoutMs: 2_147_483_648 },
			}),
		).toThrow(expect.objectContaining({ code: "invalid_configuration" }));
	});

	it("rejects an ASR chunk count that exceeds the signed 32-bit sequence domain", () => {
		const credentials = new InMemoryCredentialStore();

		expect(() =>
			createDoubaoRealtimeSpeechRecognizer({
				credentials,
				credentialReference: "doubao-account",
				endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
				authentication: { type: "api-key" },
				resourceId: "volc.bigasr.sauc.duration",
				userId: "game-runtime",
				modelName: "bigmodel",
				language: "zh-CN",
				inputFormat: INPUT_FORMAT,
				limits: { ...LIMITS, maxInputChunks: 2_147_483_647 },
			}),
		).toThrow(expect.objectContaining({ code: "invalid_configuration" }));
	});

	it("applies the endpoint byte limit after URL normalization", () => {
		const credentials = new InMemoryCredentialStore();
		const endpoint = "wss://openspeech.example.test/a b";

		expect(() =>
			createDoubaoRealtimeSpeechRecognizer({
				credentials,
				credentialReference: "doubao-account",
				endpoint,
				authentication: { type: "api-key" },
				resourceId: "volc.bigasr.sauc.duration",
				userId: "game-runtime",
				modelName: "bigmodel",
				language: "zh-CN",
				inputFormat: INPUT_FORMAT,
				limits: { ...LIMITS, maxEndpointBytes: Buffer.byteLength(endpoint, "utf8") },
			}),
		).toThrow(expect.objectContaining({ code: "invalid_configuration" }));
	});

	it.each(["maxFrameBytes", "maxWebSocketFragments", "maxWebSocketBufferedChunks"] as const)(
		"rejects %s outside the ws signed 32-bit range",
		(limitName) => {
			const credentials = new InMemoryCredentialStore();

			expect(() =>
				createDoubaoRealtimeSpeechRecognizer({
					credentials,
					credentialReference: "doubao-account",
					endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
					authentication: { type: "api-key" },
					resourceId: "volc.bigasr.sauc.duration",
					userId: "game-runtime",
					modelName: "bigmodel",
					language: "zh-CN",
					inputFormat: INPUT_FORMAT,
					limits: { ...LIMITS, [limitName]: 2_147_483_648 },
				}),
			).toThrow(expect.objectContaining({ code: "invalid_configuration" }));
		},
	);

	it("rejects a runtime configuration when any required host limit is missing", () => {
		const credentials = new InMemoryCredentialStore();
		const incompleteLimits = Object.fromEntries(
			Object.entries(LIMITS).filter(([name]) => name !== "operationTimeoutMs"),
		) as unknown as DoubaoAsrLimits;

		expect(() =>
			createDoubaoRealtimeSpeechRecognizer({
				credentials,
				credentialReference: "doubao-account",
				endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
				authentication: { type: "api-key" },
				resourceId: "volc.bigasr.sauc.duration",
				userId: "game-runtime",
				modelName: "bigmodel",
				language: "zh-CN",
				inputFormat: INPUT_FORMAT,
				limits: incompleteLimits,
			}),
		).toThrow(expect.objectContaining({ code: "invalid_configuration" }));
	});

	it("redacts failures thrown while capturing host configuration", () => {
		const credentials = new InMemoryCredentialStore();
		const options = {
			credentials,
			credentialReference: "doubao-account",
			get endpoint(): string {
				throw new Error("internal deployment details");
			},
			authentication: { type: "api-key" as const },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: LIMITS,
		};

		const failure = (() => {
			try {
				createDoubaoRealtimeSpeechRecognizer(options);
			} catch (error) {
				return error;
			}
			return undefined;
		})();

		expect(failure).toMatchObject({ code: "invalid_configuration" });
		expect(String(failure)).not.toContain("internal deployment details");
	});

	it("captures the ASR audio source and iterator methods before awaiting credentials", async () => {
		const credentialRead = Promise.withResolvers<void>();
		const releaseCredential = Promise.withResolvers<void>();
		const credentials: CredentialStore = {
			read: async () => {
				credentialRead.resolve();
				await releaseCredential.promise;
				return { type: "api_key", key: "doubao-secret" };
			},
			list: async () => [],
			modify: async () => undefined,
			delete: async () => undefined,
		};
		const reads = { source: 0, createIterator: 0, next: 0, close: 0 };
		let step = 0;
		const iterator = {
			get next() {
				reads.next += 1;
				return async () => {
					step += 1;
					return step === 1
						? { done: false as const, value: { bytes: Uint8Array.from([1, 2]) } }
						: { done: true as const, value: undefined };
				};
			},
			get return() {
				reads.close += 1;
				return async () => ({ done: true as const, value: undefined });
			},
		};
		const audio: AsyncIterable<Readonly<{ readonly bytes: Uint8Array }>> = {
			get [Symbol.asyncIterator]() {
				reads.createIterator += 1;
				return () => iterator;
			},
		};
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive: async () => ({
				type: "binary",
				bytes: asrResponse(-1, { result: [{ text: "完成" }] }, true),
			}),
			close: async () => undefined,
			abort: () => undefined,
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: LIMITS,
			connect: async () => connection,
		});
		const request = {
			format: INPUT_FORMAT,
			get audio() {
				reads.source += 1;
				return audio;
			},
			signal: new AbortController().signal,
		};

		const recognition = collect(recognizer.recognize(request));
		await credentialRead.promise;
		const readsAtCredentialAwait = { ...reads };
		releaseCredential.resolve();
		await expect(recognition).resolves.toEqual([{ type: "transcript.final", text: "完成" }]);

		expect(readsAtCredentialAwait).toEqual({ source: 1, createIterator: 1, next: 1, close: 1 });
		expect(reads).toEqual({ source: 1, createIterator: 1, next: 1, close: 1 });
	});

	it("closes a newly created ASR iterator when its next method is invalid", async () => {
		const credentials = new InMemoryCredentialStore();
		const close = vi.fn(async () => ({ done: true as const, value: undefined }));
		const audio = {
			[Symbol.asyncIterator]: () => ({
				next: 0 as unknown as AsyncIterator<Readonly<{ readonly bytes: Uint8Array }>>["next"],
				return: close,
			}),
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: LIMITS,
		});

		await expect(
			recognizer
				.recognize({ format: INPUT_FORMAT, audio, signal: new AbortController().signal })
				[Symbol.asyncIterator]()
				.next(),
		).rejects.toMatchObject({ code: "protocol" });
		await vi.waitFor(() => expect(close).toHaveBeenCalledOnce());
	});

	it("closes a newly created ASR iterator when its next getter throws", async () => {
		const credentials = new InMemoryCredentialStore();
		const close = vi.fn(async () => ({ done: true as const, value: undefined }));
		const audio = {
			[Symbol.asyncIterator]: () => ({
				get next(): AsyncIterator<Readonly<{ readonly bytes: Uint8Array }>>["next"] {
					throw new Error("internal iterator details");
				},
				return: close,
			}),
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: LIMITS,
		});

		const failure = await recognizer
			.recognize({ format: INPUT_FORMAT, audio, signal: new AbortController().signal })
			[Symbol.asyncIterator]()
			.next()
			.catch((error: unknown) => error);

		expect(failure).toMatchObject({ code: "protocol" });
		expect(String(failure)).not.toContain("internal iterator details");
		await vi.waitFor(() => expect(close).toHaveBeenCalledOnce());
	});

	it("rejects a newly created ASR iterator with a non-function return method", async () => {
		const credentials = new InMemoryCredentialStore();
		const audio = {
			[Symbol.asyncIterator]: () => ({
				next: async () => ({ done: true as const, value: undefined }),
				return: 0 as unknown as NonNullable<AsyncIterator<Readonly<{ readonly bytes: Uint8Array }>>["return"]>,
			}),
		};
		const recognizer = createDoubaoRealtimeSpeechRecognizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/sauc/bigmodel_async",
			authentication: { type: "api-key" },
			resourceId: "volc.bigasr.sauc.duration",
			userId: "game-runtime",
			modelName: "bigmodel",
			language: "zh-CN",
			inputFormat: INPUT_FORMAT,
			limits: LIMITS,
		});

		await expect(
			recognizer
				.recognize({ format: INPUT_FORMAT, audio, signal: new AbortController().signal })
				[Symbol.asyncIterator]()
				.next(),
		).rejects.toMatchObject({ code: "protocol" });
	});

	it("captures the TTS text source and iterator methods before awaiting credentials", async () => {
		const credentialRead = Promise.withResolvers<void>();
		const releaseCredential = Promise.withResolvers<void>();
		const credentials: CredentialStore = {
			read: async () => {
				credentialRead.resolve();
				await releaseCredential.promise;
				return { type: "api_key", key: "doubao-secret" };
			},
			list: async () => [],
			modify: async () => undefined,
			delete: async () => undefined,
		};
		const reads = { source: 0, createIterator: 0, next: 0, close: 0 };
		let step = 0;
		const iterator = {
			get next() {
				reads.next += 1;
				return async () => {
					step += 1;
					return step === 1 ? { done: false as const, value: "你好" } : { done: true as const, value: undefined };
				};
			},
			get return() {
				reads.close += 1;
				return async () => ({ done: true as const, value: undefined });
			},
		};
		const text = {
			get [Symbol.asyncIterator]() {
				reads.createIterator += 1;
				return () => iterator;
			},
		};
		const sessionId = "session-captured";
		const receive = vi
			.fn<DoubaoWebSocketConnection["receive"]>()
			.mockResolvedValueOnce({ type: "binary", bytes: ttsResponse(9, 50, "", {}, "connection-test") })
			.mockResolvedValueOnce({ type: "binary", bytes: ttsResponse(9, 150, sessionId, {}) })
			.mockResolvedValueOnce({ type: "binary", bytes: ttsResponse(9, 152, sessionId, {}) })
			.mockResolvedValueOnce({ type: "binary", bytes: ttsResponse(9, 52, "", {}, "connection-test") });
		const connection: DoubaoWebSocketConnection = {
			bufferedAmount: () => 0,
			send: async () => undefined,
			receive,
			close: async () => undefined,
			abort: () => undefined,
		};
		const synthesizer = createDoubaoRealtimeSpeechSynthesizer({
			credentials,
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/tts/bidirection",
			authentication: { type: "api-key" },
			resourceId: "seed-tts-2.0",
			outputFormat: OUTPUT_FORMAT,
			limits: TTS_LIMITS,
			createSessionId: () => sessionId,
			connect: async () => connection,
		});
		const request = {
			voiceProfileId: "zh_female_voice",
			format: OUTPUT_FORMAT,
			get text() {
				reads.source += 1;
				return text;
			},
			signal: new AbortController().signal,
		};

		const synthesis = collect(synthesizer.synthesize(request));
		await credentialRead.promise;
		const readsAtCredentialAwait = { ...reads };
		releaseCredential.resolve();
		await expect(synthesis).resolves.toEqual([]);

		expect(readsAtCredentialAwait).toEqual({ source: 1, createIterator: 1, next: 1, close: 1 });
		expect(reads).toEqual({ source: 1, createIterator: 1, next: 1, close: 1 });
	});

	it("captures each synthesizer configuration getter exactly once", () => {
		const credentials = new InMemoryCredentialStore();
		const connect = async () => {
			throw new Error("unused");
		};
		const createSessionId = () => "unused";
		const reads = {
			credentials: 0,
			connect: 0,
			createSessionId: 0,
			mediaType: 0,
			sampleRateHz: 0,
			channelCount: 0,
			authenticationType: 0,
			appKey: 0,
		};
		const authentication = {
			get type() {
				reads.authenticationType += 1;
				return "legacy" as const;
			},
			get appKey() {
				reads.appKey += 1;
				return "game-app";
			},
		};
		const outputFormat = {
			get mediaType() {
				reads.mediaType += 1;
				return "audio/pcm;format=s16le";
			},
			get sampleRateHz() {
				reads.sampleRateHz += 1;
				return 24_000;
			},
			get channelCount() {
				reads.channelCount += 1;
				return 1;
			},
		};
		const options = {
			get credentials() {
				reads.credentials += 1;
				return credentials;
			},
			credentialReference: "doubao-account",
			endpoint: "wss://openspeech.example.test/api/v3/tts/bidirection",
			authentication,
			resourceId: "seed-tts-2.0",
			outputFormat,
			limits: TTS_LIMITS,
			get createSessionId() {
				reads.createSessionId += 1;
				return createSessionId;
			},
			get connect() {
				reads.connect += 1;
				return connect;
			},
		};

		createDoubaoRealtimeSpeechSynthesizer(options);

		expect(reads).toEqual({
			credentials: 1,
			connect: 1,
			createSessionId: 1,
			mediaType: 1,
			sampleRateHz: 1,
			channelCount: 1,
			authenticationType: 1,
			appKey: 1,
		});
	});
});

async function* chunks(...values: readonly number[][]) {
	for (const value of values) yield { bytes: Uint8Array.from(value) };
}

async function* chunksWithValue(value: Readonly<{ readonly bytes: Uint8Array }>) {
	yield value;
}

async function* strings(...values: readonly string[]) {
	for (const value of values) yield value;
}

async function collect<T>(source: AsyncIterable<T>): Promise<T[]> {
	const values: T[] = [];
	for await (const value of source) values.push(value);
	return values;
}

function asrResponse(sequence: number, payload: unknown, last = false): Uint8Array {
	return asrResponseBytes(sequence, Buffer.from(JSON.stringify(payload)), last);
}

function asrResponseBytes(sequence: number, payload: Uint8Array, last = false): Uint8Array {
	const body = gzipSync(payload);
	const frame = Buffer.allocUnsafe(12 + body.byteLength);
	frame[0] = 0x11;
	frame[1] = last ? 0x93 : 0x91;
	frame[2] = 0x11;
	frame[3] = 0;
	frame.writeInt32BE(sequence, 4);
	frame.writeUInt32BE(body.byteLength, 8);
	body.copy(frame, 12);
	return frame;
}

function ttsResponse(
	messageType: number,
	event: number,
	sessionId: string,
	payload: unknown | Uint8Array,
	connectionId = "",
	serializationAndCompression?: number,
): Uint8Array {
	const session = Buffer.from(sessionId);
	const connection = Buffer.from(connectionId);
	const body = payload instanceof Uint8Array ? Buffer.from(payload) : Buffer.from(JSON.stringify(payload));
	const idBytes = event === 50 || event === 51 || event === 52 ? 4 + connection.byteLength : 4 + session.byteLength;
	const frame = Buffer.allocUnsafe(4 + 4 + idBytes + 4 + body.byteLength);
	frame[0] = 0x11;
	frame[1] = (messageType << 4) | 4;
	frame[2] = serializationAndCompression ?? (messageType === 11 ? 0 : 0x10);
	frame[3] = 0;
	frame.writeInt32BE(event, 4);
	frame.writeUInt32BE(event === 50 || event === 51 || event === 52 ? connection.byteLength : session.byteLength, 8);
	(event === 50 || event === 51 || event === 52 ? connection : session).copy(frame, 12);
	const payloadOffset =
		12 + (event === 50 || event === 51 || event === 52 ? connection.byteLength : session.byteLength);
	frame.writeUInt32BE(body.byteLength, payloadOffset);
	body.copy(frame, payloadOffset + 4);
	return frame;
}

function readTtsEvent(frame: Uint8Array): number {
	return Buffer.from(frame.buffer, frame.byteOffset, frame.byteLength).readInt32BE(4);
}

function readTtsJsonPayload(frame: Uint8Array): unknown {
	const buffer = Buffer.from(frame.buffer, frame.byteOffset, frame.byteLength);
	const event = buffer.readInt32BE(4);
	let offset = 8;
	if (event !== 1 && event !== 2) {
		const sessionBytes = buffer.readUInt32BE(offset);
		offset += 4 + sessionBytes;
	}
	const payloadBytes = buffer.readUInt32BE(offset);
	offset += 4;
	return JSON.parse(buffer.subarray(offset, offset + payloadBytes).toString("utf8"));
}

function closedMessage() {
	return { type: "closed" as const, code: 1000 };
}
