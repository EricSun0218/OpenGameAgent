import type { AgentTool, StreamFn } from "@earendil-works/pi-agent-core";
import type { Context } from "@earendil-works/pi-ai";
import {
	GameRealtimeVoiceService,
	type RealtimeAudioChunk,
	type RealtimeSpeechRecognizer,
	type RealtimeSpeechSynthesizer,
	type RealtimeVoiceEvent,
	type RealtimeVoiceLimits,
} from "@opengameagent/realtime";
import { GameRuntime, InMemoryEventStore } from "@opengameagent/runtime";
import { describe, expect, it } from "vitest";
import {
	allowAllTestTools,
	assistant,
	messageResponse,
	TEST_MODEL,
	TEST_TOOL_LIMITS,
	textResponse,
	tool,
	toolCall,
} from "../../devtools/src/pi-fakes.js";
import {
	GameContinuousLocalSpeechSession,
	GameLocalSpeechPipeline,
	OpenAICompatibleSpeechRecognizer,
	OpenAICompatibleSpeechSynthesizer,
	VoiceActivityUtteranceCapture,
} from "../src/index.js";

const INPUT_FORMAT = { mediaType: "audio/pcm;format=s16le", sampleRateHz: 16_000, channelCount: 1 } as const;
const OUTPUT_FORMAT = { mediaType: "audio/pcm", sampleRateHz: 24_000, channelCount: 1 } as const;
const COORDINATE = {
	sessionId: "session",
	actorId: "actor",
	voiceSessionId: "voice",
	epoch: "1",
	utteranceId: "utterance",
} as const;
const VOICE_LIMITS: RealtimeVoiceLimits = {
	maxIdentifierBytes: 128,
	maxMediaTypeBytes: 128,
	maxInputChunks: 16,
	maxInputChunkBytes: 64,
	maxInputAudioBytes: 512,
	maxRecognitionEvents: 4,
	maxRecognitionTextBytes: 256,
	maxTranscriptBytes: 128,
	maxAssistantTextEvents: 8,
	maxAssistantTextBytes: 256,
	maxPendingTextSegments: 8,
	maxOutputChunks: 8,
	maxOutputChunkBytes: 64,
	maxOutputAudioBytes: 256,
	maxEventBacklog: 32,
};
const HTTP_COMMON = {
	baseUrl: "http://localhost:8080/v1",
	apiKey: "local-secret",
	modelId: "local-model",
	requestTimeoutMs: 1_000,
	maxEndpointBytes: 256,
	maxCredentialBytes: 128,
	maxIdentifierBytes: 128,
	maxMediaTypeBytes: 128,
	maxResponseBytes: 512,
} as const;

describe("local speech to the sole Pi runtime", () => {
	it("runs VAD, one local STT request, the existing runtime, and one streamed local TTS request", async () => {
		let providerContext: Context | undefined;
		const runtime = await createRuntime((_model, context) => {
			providerContext = structuredClone(context);
			return textResponse("The gate opens.", ["The gate ", "opens."]);
		});
		let transcriptionRequests = 0;
		const recognizer = new OpenAICompatibleSpeechRecognizer({
			...HTTP_COMMON,
			inputMediaType: INPUT_FORMAT.mediaType,
			maxInputChunks: 16,
			maxInputChunkBytes: 64,
			maxInputAudioBytes: 512,
			maxTranscriptBytes: 128,
			fetch: async (input) => {
				transcriptionRequests += 1;
				const request = input as Request;
				expect(new URL(request.url).pathname).toBe("/v1/audio/transcriptions");
				const file = (await request.formData()).get("file") as File;
				expect(file.size).toBe(56);
				return Response.json({ text: "Open the gate" });
			},
		});
		let synthesisRequests = 0;
		let spokenText: string | undefined;
		const synthesizer = new OpenAICompatibleSpeechSynthesizer({
			...HTTP_COMMON,
			outputFormat: OUTPUT_FORMAT,
			responseFormat: "pcm",
			maxTextSegments: 8,
			maxTextBytes: 256,
			maxResponseChunks: 8,
			maxResponseChunkBytes: 64,
			fetch: async (input) => {
				synthesisRequests += 1;
				const request = input as Request;
				expect(new URL(request.url).pathname).toBe("/v1/audio/speech");
				spokenText = ((await request.json()) as { input: string }).input;
				return new Response(
					new ReadableStream({
						start(controller) {
							controller.enqueue(new Uint8Array([10, 11]));
							controller.enqueue(new Uint8Array([12]));
							controller.close();
						},
					}),
					{ headers: { "content-type": OUTPUT_FORMAT.mediaType } },
				);
			},
		});
		const voice = new GameRealtimeVoiceService({
			runtime,
			recognizer,
			synthesizer,
			limits: VOICE_LIMITS,
			projectInput: ({ coordinate, transcript }) => ({
				inputId: `voice:${coordinate.utteranceId}`,
				payload: { instruction: transcript },
			}),
		});
		const probabilities = [0.1, 0.9, 0.9, 0.6, 0.1, 0.1];
		const capture = new VoiceActivityUtteranceCapture({
			detector: {
				open: () => ({ detect: () => probabilities.shift() ?? 0, close: () => undefined }),
			},
			activationThreshold: 0.7,
			deactivationThreshold: 0.3,
			minSpeechFrames: 2,
			endSilenceFrames: 2,
			prefixPaddingFrames: 1,
			maxCaptureFrames: 16,
			maxCaptureBytes: 96,
			maxUtteranceFrames: 16,
			maxFrameBytes: 8,
			maxUtteranceBytes: 64,
			maxMediaTypeBytes: 128,
		});
		const pipeline = new GameLocalSpeechPipeline({ voice, capture });

		const started = await pipeline.startWhenSpeechDetected({
			coordinate: COORDINATE,
			inputFormat: INPUT_FORMAT,
			outputFormat: OUTPUT_FORMAT,
			voiceProfileId: "guard",
			audio: pcm(1, 2, 3, 4, 5, 6, 7),
		});
		expect(started.status).toBe("started");
		if (started.status !== "started") throw new Error("expected started turn");
		const events = await collect(started.turn.events);
		await expect(started.turn.completion).resolves.toMatchObject({ status: "completed", agentStatus: "completed" });

		expect({ transcriptionRequests, synthesisRequests, spokenText }).toEqual({
			transcriptionRequests: 1,
			synthesisRequests: 1,
			spokenText: "The gate opens.",
		});
		expect(providerContext?.messages.at(-1)).toMatchObject({
			role: "user",
			content: [{ type: "text", text: "Open the gate" }],
		});
		expect(events.filter(isAudio).map((event) => [...event.chunk.bytes])).toEqual([[10, 11], [12]]);
	});

	it("routes consecutive utterances through the same runtime and exact-coordinate barge-in", async () => {
		const transcripts = ["First order", "Second order"];
		const runtimeInputs: string[] = [];
		let providerCalls = 0;
		let toolCalls = 0;
		const inspect = tool("inspect", async () => {
			toolCalls += 1;
			return { content: [{ type: "text" as const, text: "inspected" }], details: {} };
		});
		const runtime = await createRuntime(
			(_model, context) => {
				const message = context.messages.at(-1);
				const input = message?.content;
				if (message?.role === "user" && typeof input === "string") runtimeInputs.push(input);
				else if (message?.role === "user" && Array.isArray(input) && input[0]?.type === "text") {
					runtimeInputs.push(input[0].text);
				}
				providerCalls += 1;
				if (providerCalls === 1) {
					return messageResponse(assistant([toolCall("call-inspect", "inspect")], "toolUse"));
				}
				return textResponse("Understood.");
			},
			{ toolSource: () => [inspect] },
		);
		const recognizer: RealtimeSpeechRecognizer = {
			async *recognize(request) {
				for await (const _chunk of request.audio) {
					// Consume the exact captured utterance.
				}
				yield { type: "transcript.final", text: transcripts.shift() ?? "unexpected" };
			},
		};
		let signalFirstSynthesis: (() => void) | undefined;
		const firstSynthesis = new Promise<void>((resolve) => {
			signalFirstSynthesis = resolve;
		});
		let signalSecondCompletion: (() => void) | undefined;
		const secondCompletion = new Promise<void>((resolve) => {
			signalSecondCompletion = resolve;
		});
		let synthesisCalls = 0;
		const synthesizer: RealtimeSpeechSynthesizer = {
			async *synthesize(request) {
				for await (const _text of request.text) {
					// Consume the runtime's visible text stream.
				}
				synthesisCalls += 1;
				yield { bytes: Uint8Array.of(synthesisCalls) };
				if (synthesisCalls === 1) {
					signalFirstSynthesis?.();
					await new Promise<void>((_, reject) => {
						const onAbort = () => reject(request.signal.reason);
						if (request.signal.aborted) onAbort();
						else request.signal.addEventListener("abort", onAbort, { once: true });
					});
				}
				signalSecondCompletion?.();
			},
		};
		const voice = new GameRealtimeVoiceService({
			runtime,
			recognizer,
			synthesizer,
			limits: VOICE_LIMITS,
			projectInput: ({ coordinate, transcript }) => ({
				inputId: `voice:${coordinate.utteranceId}`,
				payload: { instruction: transcript },
			}),
		});
		let capture = 0;
		let endpointCloses = 0;
		const continuous = new GameContinuousLocalSpeechSession({
			voice,
			endpointing: {
				openSession: async () => ({
					async captureNext(request = {}) {
						capture += 1;
						if (capture === 1) {
							await request.onSpeechStart?.();
							return { format: INPUT_FORMAT, frameCount: 1, byteLength: 2, audio: pcm(1) };
						}
						if (capture === 2) {
							await firstSynthesis;
							await request.onSpeechStart?.();
							return { format: INPUT_FORMAT, frameCount: 1, byteLength: 2, audio: pcm(2) };
						}
						await secondCompletion;
						return undefined;
					},
					close: () => {
						endpointCloses += 1;
					},
				}),
			},
			maxPendingTurns: 2,
		});
		const handle = continuous.start({
			inputFormat: INPUT_FORMAT,
			outputFormat: OUTPUT_FORMAT,
			voiceProfileId: "guard",
			audio: pcm(),
			createCoordinate: (index) => ({
				sessionId: "session",
				actorId: "actor",
				voiceSessionId: "voice",
				epoch: String(index),
				utteranceId: `utterance-${index}`,
			}),
		});

		const turns = await collect(handle.turns);
		await expect(handle.result).resolves.toEqual({ status: "source-ended" });
		expect(turns).toHaveLength(2);
		await expect(turns[0]?.turn.completion).resolves.toMatchObject({ status: "interrupted" });
		await expect(turns[1]?.turn.completion).resolves.toMatchObject({ status: "completed", agentStatus: "completed" });

		expect({ capture, endpointCloses, providerCalls, synthesisCalls, toolCalls, runtimeInputs }).toEqual({
			capture: 3,
			endpointCloses: 1,
			providerCalls: 3,
			synthesisCalls: 2,
			toolCalls: 1,
			runtimeInputs: ["First order", "Second order"],
		});
	});
});

async function createRuntime(
	modelStream: StreamFn,
	options: { readonly toolSource?: () => readonly AgentTool[] } = {},
): Promise<GameRuntime> {
	let eventId = 0;
	let agentId = 0;
	const runtime = new GameRuntime(
		new InMemoryEventStore({
			newEventId: () => `event-${++eventId}`,
			now: () => "2026-09-01T00:00:00.000Z",
		}),
		{ maxActiveActors: 1, maxQueuedRuns: 2, maxInputBytes: 4_096, maxInputDepth: 8 },
	);
	await runtime.registerActor({
		sessionId: COORDINATE.sessionId,
		actorId: COORDINATE.actorId,
		model: TEST_MODEL,
		modelStream,
		newId: () => `agent-${++agentId}`,
		now: () => 1,
		projectInput: async (input) => {
			const payload = input.payload as { instruction: string };
			return {
				message: { role: "user", content: payload.instruction, timestamp: 1 },
				transcriptContent: [{ type: "text", text: payload.instruction }],
			};
		},
		inputProjectionLimits: {
			maxContentParts: 1,
			maxTextBytes: 4_096,
			maxReferenceBytes: 1,
			maxImageBytes: 1,
			maxTotalImageBytes: 1,
		},
		...(options.toolSource
			? { toolSource: options.toolSource, toolPolicy: allowAllTestTools, toolLimits: TEST_TOOL_LIMITS }
			: {}),
	});
	return runtime;
}

async function* pcm(...samples: number[]): AsyncIterable<RealtimeAudioChunk> {
	for (const sample of samples) {
		const bytes = new Uint8Array(2);
		new DataView(bytes.buffer).setInt16(0, sample, true);
		yield { bytes };
	}
}

async function collect<T>(source: AsyncIterable<T>): Promise<T[]> {
	const values: T[] = [];
	for await (const value of source) values.push(value);
	return values;
}

function isAudio(
	event: Readonly<RealtimeVoiceEvent>,
): event is Extract<RealtimeVoiceEvent, { readonly type: "assistant.audio.chunk" }> {
	return event.type === "assistant.audio.chunk";
}
