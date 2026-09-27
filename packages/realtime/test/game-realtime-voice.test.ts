import type { AgentTool, StreamFn } from "@earendil-works/pi-agent-core";
import { type AssistantMessage, type Context, createAssistantMessageEventStream } from "@earendil-works/pi-ai";
import { RuntimeHealthMonitor } from "@opengameagent/health";
import type { RunAdmissionAuthority, StreamKey } from "@opengameagent/protocol";
import { GameRuntime, InMemoryEventStore } from "@opengameagent/runtime";
import { describe, expect, it } from "vitest";
import {
	allowAllTestTools,
	assistant,
	controlledResponse,
	messageResponse,
	TEST_MODEL,
	TEST_TOOL_LIMITS,
	textResponse,
	tool,
	toolCall,
} from "../../devtools/src/pi-fakes.js";
import {
	GameRealtimeVoiceService,
	type RealtimeAudioChunk,
	type RealtimeSpeechRecognizer,
	type RealtimeSpeechSynthesizer,
	RealtimeVoiceBusyError,
	type RealtimeVoiceEvent,
	type RealtimeVoiceHealthOptions,
	type RealtimeVoiceLimits,
} from "../src/index.js";

const STREAM: StreamKey = { sessionId: "session", actorId: "actor" };
const COORDINATE = {
	...STREAM,
	voiceSessionId: "voice-session",
	epoch: "1",
	utteranceId: "utterance-1",
} as const;
const INPUT_FORMAT = { mediaType: "audio/pcm", sampleRateHz: 16_000, channelCount: 1 } as const;
const OUTPUT_FORMAT = { mediaType: "audio/pcm", sampleRateHz: 24_000, channelCount: 1 } as const;
const LIMITS: RealtimeVoiceLimits = {
	maxIdentifierBytes: 128,
	maxMediaTypeBytes: 128,
	maxInputChunks: 8,
	maxInputChunkBytes: 64,
	maxInputAudioBytes: 256,
	maxRecognitionEvents: 8,
	maxRecognitionTextBytes: 256,
	maxTranscriptBytes: 128,
	maxAssistantTextEvents: 16,
	maxAssistantTextBytes: 512,
	maxPendingTextSegments: 8,
	maxOutputChunks: 8,
	maxOutputChunkBytes: 64,
	maxOutputAudioBytes: 256,
	maxEventBacklog: 32,
};

describe("GameRealtimeVoiceService", () => {
	it("reports real recognizer and synthesizer operations without active health probes", async () => {
		let monotonicNow = 100;
		let recognitionCalls = 0;
		let synthesisCalls = 0;
		const recognizer: RealtimeSpeechRecognizer = {
			async *recognize(request) {
				recognitionCalls += 1;
				for await (const _chunk of request.audio) {
					// consume
				}
				yield { type: "transcript.final", text: "Open" };
			},
		};
		const synthesizer: RealtimeSpeechSynthesizer = {
			async *synthesize(request) {
				synthesisCalls += 1;
				for await (const _text of request.text) {
					// consume
				}
				yield { bytes: bytes([1]) };
			},
		};
		const runtime = await createRuntime(() => textResponse("Opened"));
		const service = createService(runtime, recognizer, synthesizer, LIMITS, {
			now: () => monotonicNow,
			recognizer: {
				id: "speech-recognition",
				requirement: "required",
				maxObservationAgeMs: 5,
			},
			synthesizer: {
				id: "speech-synthesis",
				requirement: "required",
				maxObservationAgeMs: 5,
			},
		});
		const health = new RuntimeHealthMonitor({
			dependencies: service.healthDependencies(),
			limits: {
				maxDependencies: 2,
				maxDependencyIdBytes: 64,
				maxConcurrentInspections: 2,
				inspectionTimeoutMs: 1_000,
			},
			now: () => Date.parse("2026-09-01T00:00:00.000Z"),
		});

		await expect(health.inspect()).resolves.toMatchObject({
			status: "not-ready",
			dependencies: [{ state: "declared" }, { state: "declared" }],
		});
		expect({ recognitionCalls, synthesisCalls }).toEqual({ recognitionCalls: 0, synthesisCalls: 0 });

		const turn = service.start(request(audio(bytes([1]))));
		await collect(turn.events);
		await expect(turn.completion).resolves.toMatchObject({ status: "completed" });
		await expect(health.inspect()).resolves.toMatchObject({
			status: "ready",
			dependencies: [{ state: "ready" }, { state: "ready" }],
		});
		expect({ recognitionCalls, synthesisCalls }).toEqual({ recognitionCalls: 1, synthesisCalls: 1 });

		monotonicNow = 106;
		await expect(health.inspect()).resolves.toMatchObject({
			status: "not-ready",
			dependencies: [
				{ state: "available", reason: "stale" },
				{ state: "available", reason: "stale" },
			],
		});
		expect({ recognitionCalls, synthesisCalls }).toEqual({ recognitionCalls: 1, synthesisCalls: 1 });
	});

	it("keeps passive realtime failure evidence scoped to the provider stage that failed", async () => {
		const runtime = await createRuntime(() => textResponse("Reply"));
		const healthOptions: RealtimeVoiceHealthOptions = {
			now: () => 1,
			recognizer: { id: "recognizer", requirement: "required", maxObservationAgeMs: 10 },
			synthesizer: { id: "synthesizer", requirement: "required", maxObservationAgeMs: 10 },
		};
		const recognitionFailure = createService(
			runtime,
			{
				recognize() {
					throw new Error("offline");
				},
			},
			oneChunkSynthesizer(),
			LIMITS,
			healthOptions,
		);
		const failedRecognition = recognitionFailure.start(request(audio(bytes([1]))));
		await collect(failedRecognition.events);
		await expect(failedRecognition.completion).resolves.toMatchObject({ status: "failed", failure: "recognition" });
		const recognitionStates = await Promise.all(
			recognitionFailure.healthDependencies().map((dependency) => dependency.inspect?.(new AbortController().signal)),
		);
		expect(recognitionStates).toEqual([{ state: "unavailable", reason: "dependency-failure" }, { state: "declared" }]);

		const synthesisFailure = createService(
			runtime,
			finalRecognizer("Speak"),
			emptySynthesizer(),
			LIMITS,
			healthOptions,
		);
		const failedSynthesis = synthesisFailure.start(request(audio(bytes([1]))));
		await collect(failedSynthesis.events);
		await expect(failedSynthesis.completion).resolves.toMatchObject({ status: "failed", failure: "synthesis" });
		const synthesisStates = await Promise.all(
			synthesisFailure.healthDependencies().map((dependency) => dependency.inspect?.(new AbortController().signal)),
		);
		expect(synthesisStates).toEqual([{ state: "ready" }, { state: "unavailable", reason: "dependency-failure" }]);
	});

	it("replaces prior recognition readiness when the recognizer emits an empty final transcript", async () => {
		let empty = false;
		const runtime = await createRuntime(() => textResponse("Reply"));
		const service = createService(
			runtime,
			{
				async *recognize(request) {
					for await (const _chunk of request.audio) {
						// consume
					}
					yield { type: "transcript.final", text: empty ? "" : "Speak" };
				},
			},
			oneChunkSynthesizer(),
			LIMITS,
			{
				now: () => 1,
				recognizer: { id: "recognizer", requirement: "required", maxObservationAgeMs: 10 },
				synthesizer: { id: "synthesizer", requirement: "optional", maxObservationAgeMs: 10 },
			},
		);
		const healthy = service.start(request(audio(bytes([1]))));
		await collect(healthy.events);
		await expect(healthy.completion).resolves.toMatchObject({ status: "completed" });
		await expect(service.healthDependencies()[0]?.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "ready",
		});

		empty = true;
		const invalid = service.start(request(audio(bytes([1]))));
		await collect(invalid.events);
		await expect(invalid.completion).resolves.toMatchObject({ status: "failed", failure: "invalid_transcript" });
		await expect(service.healthDependencies()[0]?.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "unavailable",
			reason: "dependency-failure",
		});
	});

	it("does not report synthesis ready when the provider ends before consuming all visible text", async () => {
		const runtime = await createRuntime(() => textResponse("AB", ["A", "B"]));
		const service = createService(
			runtime,
			finalRecognizer("Speak"),
			{
				async *synthesize(request) {
					for await (const _text of request.text) {
						yield { bytes: bytes([1]) };
						return;
					}
				},
			},
			LIMITS,
			{
				now: () => 1,
				recognizer: { id: "recognizer", requirement: "optional", maxObservationAgeMs: 10 },
				synthesizer: { id: "synthesizer", requirement: "required", maxObservationAgeMs: 10 },
			},
		);
		const turn = service.start(request(audio(bytes([1]))));

		await collect(turn.events);
		await expect(turn.completion).resolves.toMatchObject({ status: "failed", failure: "synthesis" });
		await expect(service.healthDependencies()[1]?.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "unavailable",
			reason: "dependency-failure",
		});
	});

	it("preserves prior synthesis readiness when local text backpressure aborts a waiting provider", async () => {
		let response = 0;
		let stall = false;
		const runtime = await createRuntime(() =>
			response++ === 0 ? textResponse("Ready") : textResponse("AB", ["A", "B"]),
		);
		const service = createService(
			runtime,
			finalRecognizer("Speak"),
			{
				async *synthesize(request) {
					if (stall) {
						await new Promise<void>((_resolve, reject) => {
							if (request.signal.aborted) reject(new Error("cancelled by coordinator"));
							else
								request.signal.addEventListener("abort", () => reject(new Error("cancelled by coordinator")), {
									once: true,
								});
						});
						return;
					}
					for await (const _text of request.text) {
						// consume
					}
					yield { bytes: bytes([1]) };
				},
			},
			{ ...LIMITS, maxPendingTextSegments: 1 },
			{
				now: () => 1,
				recognizer: { id: "recognizer", requirement: "optional", maxObservationAgeMs: 10 },
				synthesizer: { id: "synthesizer", requirement: "required", maxObservationAgeMs: 10 },
			},
		);
		const healthy = service.start(request(audio(bytes([1]))));
		await collect(healthy.events);
		await expect(healthy.completion).resolves.toMatchObject({ status: "completed" });
		await expect(service.healthDependencies()[1]?.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "ready",
		});

		stall = true;
		const backpressured = service.start(request(audio(bytes([1]))));
		await collect(backpressured.events);
		await expect(backpressured.completion).resolves.toMatchObject({ status: "failed", failure: "text_backpressure" });
		await new Promise<void>((resolve) => setImmediate(resolve));
		await expect(service.healthDependencies()[1]?.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "ready",
		});
	});

	it("feeds one final transcript into the Pi runtime and streams only visible assistant text to TTS", async () => {
		let providerContext: Context | undefined;
		const runtime = await createRuntime((_model, context) => {
			providerContext = structuredClone(context);
			return textResponse("The gate opens.", ["The gate ", "opens."]);
		});
		const inputBytes: number[] = [];
		const synthesized: string[] = [];
		const recognizer: RealtimeSpeechRecognizer = {
			async *recognize(request) {
				expect(request.format).toEqual(INPUT_FORMAT);
				for await (const chunk of request.audio) inputBytes.push(...chunk.bytes);
				yield { type: "transcript.delta", text: "Open" };
				yield { type: "transcript.final", text: "Open the gate" };
			},
		};
		const synthesizer: RealtimeSpeechSynthesizer = {
			async *synthesize(request) {
				expect(request.format).toEqual(OUTPUT_FORMAT);
				expect(request.voiceProfileId).toBe("guard-voice");
				for await (const text of request.text) synthesized.push(text);
				yield { bytes: bytes([10, 11]) };
				yield { bytes: bytes([12]) };
			},
		};
		const service = createService(runtime, recognizer, synthesizer);
		const turn = service.start({
			coordinate: COORDINATE,
			inputFormat: INPUT_FORMAT,
			outputFormat: OUTPUT_FORMAT,
			voiceProfileId: "guard-voice",
			audio: audio(bytes([1, 2]), bytes([3])),
		});

		const events = await collect(turn.events);
		await expect(turn.completion).resolves.toMatchObject({ status: "completed", agentStatus: "completed" });
		expect(providerContext?.messages.at(-1)).toMatchObject({
			role: "user",
			content: [{ type: "text", text: "Open the gate" }],
		});
		expect(inputBytes).toEqual([1, 2, 3]);
		expect(synthesized).toEqual(["The gate ", "opens."]);
		expect(events.map((event) => event.type)).toEqual([
			"input.transcript.delta",
			"input.transcript.final",
			"agent.started",
			"assistant.text.delta",
			"assistant.text.delta",
			"assistant.audio.chunk",
			"assistant.audio.chunk",
			"assistant.audio.completed",
			"turn.completed",
		]);
		expect(events.filter(isAudioChunk).map((event) => [...event.chunk.bytes])).toEqual([[10, 11], [12]]);
	});

	it("speaks ordinary assistant text while excluding reasoning summaries and Tool internals", async () => {
		const privateValues = ["private reasoning", "private tool details", "private replay envelope"] as const;
		const inspect = tool("inspect", async () => ({
			content: [{ type: "text", text: "private tool result" }],
			details: { secret: privateValues[1] },
		}));
		let call = 0;
		const runtime = await createRuntime(
			() => {
				call += 1;
				if (call === 1) {
					return messageResponse(
						assistant(
							[
								{ type: "thinking", thinking: privateValues[0] },
								{ type: "text", text: "Visible before. " },
								toolCall("call-inspect", "inspect"),
								{ type: "text", text: "Visible after. " },
							],
							"toolUse",
						),
					);
				}
				return messageResponse(
					assistant([
						{
							type: "thinking",
							thinking: "Checked the result.",
							thinkingSignature: JSON.stringify({
								id: "rs_private",
								type: "reasoning",
								summary: [{ type: "summary_text", text: "Checked the result." }],
								content: [],
								encrypted_content: privateValues[2],
							}),
						},
						{ type: "text", text: "Final visible." },
					]),
				);
			},
			{ toolSource: () => [inspect] },
		);
		const spoken: string[] = [];
		const service = createService(runtime, finalRecognizer("Inspect"), {
			async *synthesize(request) {
				for await (const text of request.text) spoken.push(text);
				yield { bytes: bytes([1]) };
			},
		});
		const turn = service.start(request(audio(bytes([1]))));

		const events = await collect(turn.events);
		await expect(turn.completion).resolves.toMatchObject({ status: "completed" });
		expect(spoken.join("")).toBe("Visible before. Visible after. Final visible.");
		const serialized = JSON.stringify(events);
		for (const value of privateValues) expect(serialized).not.toContain(value);
		expect(serialized).not.toContain("private tool result");
		expect(serialized).not.toContain("Checked the result.");
	});

	it("never invokes TTS for an empty or tool-only visible response", async () => {
		const runtime = await createRuntime(() => terminalResponse(assistant([], "stop")));
		let syntheses = 0;
		const service = createService(runtime, finalRecognizer("Wait"), {
			async *synthesize() {
				syntheses += 1;
				yield { bytes: bytes([1]) };
			},
		});
		const turn = service.start(request(audio(bytes([1]))));

		const events = await collect(turn.events);
		await expect(turn.completion).resolves.toMatchObject({ status: "completed" });
		expect(syntheses).toBe(0);
		expect(events.some((event) => event.type.startsWith("assistant.audio"))).toBe(false);
	});

	it("synthesizes a final visible message even when the provider emitted no text deltas", async () => {
		const runtime = await createRuntime(() => finalTextWithoutDelta("Final only"));
		const synthesized: string[] = [];
		const service = createService(runtime, finalRecognizer("Continue"), {
			async *synthesize(request) {
				for await (const text of request.text) synthesized.push(text);
				yield { bytes: bytes([1]) };
			},
		});
		const turn = service.start(request(audio(bytes([1]))));

		await collect(turn.events);
		await expect(turn.completion).resolves.toMatchObject({ status: "completed" });
		expect(synthesized).toEqual(["Final only"]);
	});

	it("fails when a streamed visible delta is missing from the canonical final item", async () => {
		const runtime = await createRuntime(() => inconsistentFinalText("leaked"));
		const service = createService(runtime, finalRecognizer("Continue"), oneChunkSynthesizer());
		const turn = service.start(request(audio(bytes([1]))));

		const events = await collect(turn.events);
		await expect(turn.completion).resolves.toMatchObject({ status: "failed", failure: "agent" });
		expect(events.some((event) => event.type === "turn.completed" && event.status === "failed")).toBe(true);
	});

	it("does not claim audio completion when a synthesizer returns no audio", async () => {
		const runtime = await createRuntime(() => textResponse("Audible"));
		const service = createService(runtime, finalRecognizer("Continue"), emptySynthesizer());
		const turn = service.start(request(audio(bytes([1]))));

		const events = await collect(turn.events);
		await expect(turn.completion).resolves.toMatchObject({ status: "failed", failure: "synthesis" });
		expect(events.some((event) => event.type === "assistant.audio.completed")).toBe(false);
	});

	it("ignores empty provider audio fragments without claiming them as playout", async () => {
		const runtime = await createRuntime(() => textResponse("Audible"));
		const service = createService(runtime, finalRecognizer("Continue"), {
			async *synthesize(request) {
				for await (const _text of request.text) {
					// consume
				}
				yield { bytes: bytes([]) };
				yield { bytes: bytes([7]) };
			},
		});
		const turn = service.start(request(audio(bytes([1]))));

		const events = await collect(turn.events);
		await expect(turn.completion).resolves.toMatchObject({ status: "completed" });
		expect(events.filter(isAudioChunk).map((event) => [...event.chunk.bytes])).toEqual([[7]]);
		expect(events.find((event) => event.type === "assistant.audio.completed")).toMatchObject({
			chunks: "1",
			bytes: "1",
		});
	});

	it("preserves exact voice fencing so a stale interrupt cannot stop a newer utterance", async () => {
		let release: (() => void) | undefined;
		const stalled = new Promise<void>((resolve) => {
			release = resolve;
		});
		const runtime = await createRuntime(() => textResponse("Done"));
		const service = createService(
			runtime,
			{
				async *recognize() {
					await stalled;
					yield { type: "transcript.final", text: "Continue" };
				},
			},
			oneChunkSynthesizer(),
		);
		const turn = service.start(request(audio(bytes([1]))));

		await expect(service.interrupt({ ...COORDINATE, utteranceId: "older" }, new Error("stale"))).resolves.toEqual({
			accepted: false,
			reason: "coordinate-mismatch",
			activeCoordinate: COORDINATE,
		});
		release?.();
		await collect(turn.events);
		await expect(turn.completion).resolves.toMatchObject({ status: "completed" });
	});

	it("interrupts a stalled recognizer with the caller's exact reason", async () => {
		const reason = new Error("player started speaking again");
		let observedReason: unknown;
		const runtime = await createRuntime(() => textResponse("unused"));
		const recognizer: RealtimeSpeechRecognizer = {
			recognize(request) {
				request.signal.addEventListener("abort", () => {
					observedReason = request.signal.reason;
				});
				return {
					[Symbol.asyncIterator]() {
						return {
							next: () => new Promise<IteratorResult<never>>(() => undefined),
						};
					},
				};
			},
		};
		const service = createService(runtime, recognizer, emptySynthesizer());
		const turn = service.start(request(audio(bytes([1]))));

		await expect(service.interrupt(COORDINATE, reason)).resolves.toEqual({ accepted: true, coordinate: COORDINATE });
		await collect(turn.events);
		await expect(turn.completion).resolves.toMatchObject({ status: "interrupted" });
		expect(observedReason).toBe(reason);
	});

	it("awaits one abort-aware input projection after final recognition before starting the runtime", async () => {
		let runtimeStarts = 0;
		const runtime = {
			start(input: { sessionId: string; actorId: string }) {
				runtimeStarts += 1;
				return {
					runId: "run-1",
					initialCoordinate: { ...input, runId: "run-1", turn: "0" },
					completion: Promise.resolve({ runId: "run-1", status: "completed" as const }),
				};
			},
			abortRun: async () => ({ accepted: false as const, reason: "no-active-run" as const }),
			subscribeImmediate: () => () => undefined,
		} as unknown as GameRuntime;
		let resolveProjection:
			| ((projection: { readonly inputId: string; readonly payload: Readonly<Record<string, never>> }) => void)
			| undefined;
		const projection = new Promise<{
			readonly inputId: string;
			readonly payload: Readonly<Record<string, never>>;
		}>((resolve) => {
			resolveProjection = resolve;
		});
		let projectedRequest: unknown;
		let projectedSignal: AbortSignal | undefined;
		let markProjectionStarted: (() => void) | undefined;
		const projectionStarted = new Promise<void>((resolve) => {
			markProjectionStarted = resolve;
		});
		const service = new GameRealtimeVoiceService({
			runtime,
			recognizer: finalRecognizer("Open the gate"),
			synthesizer: emptySynthesizer(),
			limits: LIMITS,
			projectInput: (input, signal) => {
				projectedRequest = input;
				projectedSignal = signal;
				markProjectionStarted?.();
				return projection;
			},
		});

		const turn = service.start(request(audio(bytes([1]))));
		await projectionStarted;

		expect(projectedRequest).toEqual({ coordinate: COORDINATE, transcript: "Open the gate" });
		expect(projectedSignal).toBeInstanceOf(AbortSignal);
		expect(projectedSignal?.aborted).toBe(false);
		expect(runtimeStarts).toBe(0);

		resolveProjection?.({ inputId: "latest-world", payload: {} });
		await expect(turn.completion).resolves.toMatchObject({ status: "completed", agentStatus: "completed" });
		expect(runtimeStarts).toBe(1);
	});

	it("captures projected admission authority and closes only that generation on the same voice channel", async () => {
		const response = controlledResponse();
		let providerSignal: AbortSignal | undefined;
		let providerCalls = 0;
		const runtime = await createRuntime((model, context, options) => {
			if (providerCalls++ > 0) return textResponse("Recovered");
			providerSignal = options?.signal;
			return response.streamFn(model, context, options);
		});
		const authority = { scope: "planning", generation: "1" };
		const projection = {
			inputId: "voice-planning-1",
			payload: { instruction: "Continue" },
			admissionAuthority: authority,
		};
		const service = new GameRealtimeVoiceService({
			runtime,
			recognizer: finalRecognizer("Continue"),
			synthesizer: oneChunkSynthesizer(),
			limits: LIMITS,
			projectInput: () => projection,
		});
		const turn = service.start(request(audio(bytes([1]))));
		const events = collect(turn.events);
		try {
			await response.started;
			authority.scope = "changed-after-start";
			authority.generation = "2";
			projection.admissionAuthority = { scope: "replaced-after-start", generation: "3" };
			const page = await runtime.readEvents(STREAM, { limit: LIMITS.maxEventBacklog });
			expect(page.events.find((event) => event.data.type === "run.queued")?.data).toMatchObject({
				admissionAuthority: { scope: "planning", generation: "1" },
			});

			await runtime.closeAuthority({ ...STREAM, scope: "planning", generation: "1", operationId: "close-planning-1" });
			expect(providerSignal?.aborted).toBe(true);
			await expect(turn.completion).resolves.toMatchObject({ status: "interrupted", agentStatus: "aborted" });
			expect((await events).at(-1)).toMatchObject({ type: "turn.completed", status: "interrupted" });

			projection.inputId = "voice-planning-1-closed";
			projection.admissionAuthority = { scope: "planning", generation: "1" };
			const rejected = service.start({
				...request(audio(bytes([2]))),
				coordinate: { ...COORDINATE, utteranceId: "closed-utterance" },
			});
			await collect(rejected.events);
			await expect(rejected.completion).resolves.toMatchObject({ status: "failed", failure: "agent" });
			expect(providerCalls).toBe(1);

			projection.inputId = "voice-planning-2";
			projection.admissionAuthority = { scope: "planning", generation: "2" };
			const recovered = service.start({
				...request(audio(bytes([2]))),
				coordinate: { ...COORDINATE, epoch: "2", utteranceId: "utterance-2" },
			});
			await collect(recovered.events);
			await expect(recovered.completion).resolves.toMatchObject({ status: "completed" });
			const finalPage = await runtime.readEvents(STREAM, { limit: LIMITS.maxEventBacklog });
			expect(
				finalPage.events.filter((event) => event.data.type === "run.queued").map((event) => event.data),
			).toMatchObject([
				{ admissionAuthority: { scope: "planning", generation: "1" } },
				{ admissionAuthority: { scope: "planning", generation: "2" } },
			]);
		} finally {
			await service.interrupt(COORDINATE, new Error("test cleanup"));
			await turn.completion;
			await events;
		}
	});

	it.each([null, { scope: "", generation: "1" }, { scope: "planning", generation: "01" }])(
		"rejects invalid projected admission authority before run.queued: %j",
		async (admissionAuthority) => {
			let providerCalls = 0;
			const runtime = await createRuntime(() => {
				providerCalls += 1;
				return textResponse("unused");
			});
			const service = new GameRealtimeVoiceService({
				runtime,
				recognizer: finalRecognizer("Continue"),
				synthesizer: oneChunkSynthesizer(),
				limits: LIMITS,
				projectInput: () => ({
					inputId: "invalid-authority",
					payload: { instruction: "Continue" },
					admissionAuthority: admissionAuthority as unknown as RunAdmissionAuthority,
				}),
			});
			const turn = service.start(request(audio(bytes([1]))));
			await collect(turn.events);
			await expect(turn.completion).resolves.toMatchObject({ status: "failed", failure: "agent" });
			expect((await runtime.readEvents(STREAM, { limit: LIMITS.maxEventBacklog })).events).toEqual([]);
			expect(providerCalls).toBe(0);
		},
	);

	it.each(["inputId", "payload", "admissionAuthority", "scope", "generation"] as const)(
		"does not admit a run when the projected %s getter cancels the voice turn",
		async (field) => {
			const caller = new AbortController();
			const reason = new Error("cancelled while capturing projection");
			let providerCalls = 0;
			const runtime = await createRuntime(() => {
				providerCalls += 1;
				return textResponse("Recovered");
			});
			const projection = {
				inputId: "voice-projected",
				payload: { instruction: "Continue" },
				admissionAuthority: { scope: "planning", generation: "1" },
			};
			const target = field === "scope" || field === "generation" ? projection.admissionAuthority : projection;
			const value: unknown = Reflect.get(target, field);
			let getterReads = 0;
			Object.defineProperty(target, field, {
				enumerable: true,
				get() {
					getterReads += 1;
					caller.abort(reason);
					return value;
				},
			});
			const service = new GameRealtimeVoiceService({
				runtime,
				recognizer: finalRecognizer("Continue"),
				synthesizer: oneChunkSynthesizer(),
				limits: LIMITS,
				projectInput: () => projection,
			});
			const turn = service.start({ ...request(audio(bytes([1]))), signal: caller.signal });
			await collect(turn.events);
			await expect(turn.completion).resolves.toMatchObject({ status: "interrupted" });
			expect(caller.signal.reason).toBe(reason);
			expect(getterReads).toBe(1);
			expect((await runtime.readEvents(STREAM, { limit: LIMITS.maxEventBacklog })).events).toEqual([]);
			expect(providerCalls).toBe(0);

			const recovered = service.start({
				...request(audio(bytes([2]))),
				coordinate: { ...COORDINATE, epoch: "2", utteranceId: "utterance-2" },
			});
			await collect(recovered.events);
			await expect(recovered.completion).resolves.toMatchObject({ status: "completed" });
			expect(providerCalls).toBe(1);
		},
	);

	it("aborts the admitted run when the runtime identifier callback cancels before returning its coordinate", async () => {
		const caller = new AbortController();
		const reason = new Error("cancelled inside runtime start");
		let runIds = 0;
		let providerCalls = 0;
		const runtime = await createRuntime(
			() => {
				providerCalls += 1;
				return textResponse("Recovered");
			},
			{
				newRunId: () => {
					caller.abort(reason);
					return `run-${++runIds}`;
				},
			},
		);
		const service = createService(runtime, finalRecognizer("Continue"), oneChunkSynthesizer());
		const turn = service.start({ ...request(audio(bytes([1]))), signal: caller.signal });
		await collect(turn.events);
		await expect(turn.completion).resolves.toMatchObject({ status: "interrupted", agentStatus: "aborted" });
		expect(providerCalls).toBe(0);
		const page = await runtime.readEvents(STREAM, { limit: LIMITS.maxEventBacklog });
		expect(page.events.filter((event) => event.data.type === "run.completed").map((event) => event.data)).toEqual([
			{ type: "run.completed", status: "aborted" },
		]);
		expect(runtime.readActiveCoordinate(STREAM)).toBeUndefined();

		const recovered = service.start({
			...request(audio(bytes([2]))),
			coordinate: { ...COORDINATE, epoch: "2", utteranceId: "utterance-2" },
		});
		await collect(recovered.events);
		await expect(recovered.completion).resolves.toMatchObject({ status: "completed" });
		expect(providerCalls).toBe(1);
	});

	it("waits for the admitted run when cancellation cannot persist its control event", async () => {
		const caller = new AbortController();
		const response = controlledResponse();
		let controlWrites = 0;
		const eventStore = new (class extends InMemoryEventStore {
			override async append(...args: Parameters<InMemoryEventStore["append"]>) {
				if (args[3].type === "control.accepted") {
					controlWrites += 1;
					throw new Error("control event storage unavailable");
				}
				return super.append(...args);
			}
		})();
		const runtime = await createRuntime(response.streamFn, {
			eventStore,
			newRunId: () => {
				caller.abort(new Error("cancelled inside runtime start"));
				return "run-cancel-storage-failure";
			},
		});
		const terminal = Promise.withResolvers<void>();
		const unsubscribe = runtime.subscribeImmediate(STREAM, (event) => {
			if (event.data.type === "run.completed") terminal.resolve();
		});
		const service = createService(runtime, finalRecognizer("Continue"), oneChunkSynthesizer());
		const turn = service.start({ ...request(audio(bytes([1]))), signal: caller.signal });
		const events = collect(turn.events);
		let voiceSettled = false;
		const completion = turn.completion.then((result) => {
			voiceSettled = true;
			return result;
		});
		try {
			await response.started;
			await new Promise<void>((resolve) => setImmediate(resolve));
			expect(controlWrites).toBe(1);
			expect(runtime.readActiveCoordinate(STREAM)).toMatchObject({ runId: "run-cancel-storage-failure" });
			expect(voiceSettled).toBe(false);

			response.finish("Finished after the failed cancellation");
			await expect(completion).resolves.toMatchObject({ status: "failed", failure: "agent", agentStatus: "completed" });
			expect((await events).at(-1)).toMatchObject({ type: "turn.completed", status: "failed", failure: "agent" });
			expect(controlWrites).toBe(1);
		} finally {
			response.finish("test cleanup");
			await terminal.promise;
			await completion;
			await events;
			unsubscribe();
		}
	});

	it("interrupts an unsettled input projection and ignores its late result", async () => {
		const reason = new Error("player barged in while loading world state");
		let runtimeStarts = 0;
		const runtime = {
			...undefinedRuntime(),
			start: () => {
				runtimeStarts += 1;
				throw new Error("must not start");
			},
		} as unknown as GameRuntime;
		let resolveProjection:
			| ((projection: { readonly inputId: string; readonly payload: Readonly<Record<string, never>> }) => void)
			| undefined;
		const projection = new Promise<{
			readonly inputId: string;
			readonly payload: Readonly<Record<string, never>>;
		}>((resolve) => {
			resolveProjection = resolve;
		});
		let observedReason: unknown;
		let markProjectionStarted: (() => void) | undefined;
		const projectionStarted = new Promise<void>((resolve) => {
			markProjectionStarted = resolve;
		});
		const service = new GameRealtimeVoiceService({
			runtime,
			recognizer: finalRecognizer("Open the gate"),
			synthesizer: emptySynthesizer(),
			limits: LIMITS,
			projectInput: (_input, signal) => {
				signal.addEventListener("abort", () => {
					observedReason = signal.reason;
				});
				markProjectionStarted?.();
				return projection;
			},
		});
		const turn = service.start(request(audio(bytes([1]))));
		await projectionStarted;

		await expect(service.interrupt(COORDINATE, reason)).resolves.toEqual({
			accepted: true,
			coordinate: COORDINATE,
		});
		await expect(turn.completion).resolves.toMatchObject({ status: "interrupted" });
		expect(observedReason).toBe(reason);

		resolveProjection?.({ inputId: "stale-world", payload: {} });
		await Promise.resolve();
		expect(runtimeStarts).toBe(0);
	});

	it("does not start a runtime when cancellation wins between projection settlement and continuation", async () => {
		const reason = new Error("cancelled at the projection boundary");
		const caller = new AbortController();
		let runtimeStarts = 0;
		const runtime = {
			...undefinedRuntime(),
			start: () => {
				runtimeStarts += 1;
				throw new Error("must not start");
			},
		} as unknown as GameRuntime;
		let resolveProjection:
			| ((projection: { readonly inputId: string; readonly payload: Readonly<Record<string, never>> }) => void)
			| undefined;
		const projection = new Promise<{
			readonly inputId: string;
			readonly payload: Readonly<Record<string, never>>;
		}>((resolve) => {
			resolveProjection = resolve;
		});
		let markProjectionStarted: (() => void) | undefined;
		const projectionStarted = new Promise<void>((resolve) => {
			markProjectionStarted = resolve;
		});
		const service = new GameRealtimeVoiceService({
			runtime,
			recognizer: finalRecognizer("Open the gate"),
			synthesizer: emptySynthesizer(),
			limits: LIMITS,
			projectInput: () => {
				markProjectionStarted?.();
				return projection;
			},
		});
		const turn = service.start({ ...request(audio(bytes([1]))), signal: caller.signal });
		await projectionStarted;
		void projection.then(() => caller.abort(reason));

		resolveProjection?.({ inputId: "stale-world", payload: {} });
		await expect(turn.completion).resolves.toMatchObject({ status: "interrupted" });
		expect(caller.signal.reason).toBe(reason);
		expect(runtimeStarts).toBe(0);
	});

	it("aborts the exact active Pi run with the caller's interruption reason", async () => {
		const response = controlledResponse();
		let providerSignal: AbortSignal | undefined;
		const runtime = await createRuntime((model, context, options) => {
			providerSignal = options?.signal;
			return response.streamFn(model, context, options);
		});
		const service = createService(runtime, finalRecognizer("Stop speaking"), emptySynthesizer());
		const turn = service.start(request(audio(bytes([1]))));
		const eventsPromise = collect(turn.events);
		await response.started;

		const reason = new Error("player barged in");
		await expect(service.interrupt(COORDINATE, reason)).resolves.toEqual({
			accepted: true,
			coordinate: COORDINATE,
		});

		await expect(turn.completion).resolves.toMatchObject({ status: "interrupted", agentStatus: "aborted" });
		expect(providerSignal?.aborted).toBe(true);
		expect(providerSignal?.reason).toBe(reason);
		expect((await eventsPromise).map((event) => event.type)).toContain("turn.completed");
		expect(runtime.readActiveCoordinate(STREAM)).toBeUndefined();
	});

	it("keeps an un-interrupted channel busy without touching the rejected audio iterator", async () => {
		const runtime = await createRuntime(() => textResponse("unused"));
		const service = createService(
			runtime,
			{
				recognize() {
					return neverEndingEvents();
				},
			},
			emptySynthesizer(),
		);
		const first = service.start(request(audio(bytes([1]))));
		let audioTouched = false;
		const rejectedAudio = {
			get [Symbol.asyncIterator]() {
				audioTouched = true;
				return async function* () {
					yield { bytes: bytes([2]) };
				};
			},
		};

		expect(() =>
			service.start({ ...request(rejectedAudio), coordinate: { ...COORDINATE, utteranceId: "utterance-2" } }),
		).toThrowError(RealtimeVoiceBusyError);
		expect(audioTouched).toBe(false);

		await service.interrupt(COORDINATE, new Error("cleanup"));
		await collect(first.events);
		await expect(first.completion).resolves.toMatchObject({ status: "interrupted" });
	});

	it("admits a newer exact coordinate after interrupt even while the old runtime completion stalls", async () => {
		const stalled = stallingRuntime();
		const service = createService(stalled.runtime, finalRecognizer("Continue"), emptySynthesizer());
		const oldTurn = service.start(request(audio(bytes([1]))));
		await stalled.firstStarted;

		await expect(service.interrupt(COORDINATE, new Error("barge in"))).resolves.toMatchObject({ accepted: true });
		const newerCoordinate = { ...COORDINATE, epoch: "2", utteranceId: "utterance-2" } as const;
		const newerTurn = service.start({ ...request(audio(bytes([2]))), coordinate: newerCoordinate });

		await collect(newerTurn.events);
		await expect(newerTurn.completion).resolves.toMatchObject({ status: "completed" });
		stalled.releaseFirst();
		await collect(oldTurn.events);
		await expect(oldTurn.completion).resolves.toMatchObject({ status: "interrupted" });
	});

	it("checks an already-aborted signal before touching the audio source", () => {
		const reason = new Error("already stopped");
		const controller = new AbortController();
		controller.abort(reason);
		let audioTouched = false;
		const audioSource = {
			get [Symbol.asyncIterator]() {
				audioTouched = true;
				return async function* () {
					yield { bytes: bytes([1]) };
				};
			},
		};
		const service = createService(undefinedRuntime(), finalRecognizer("unused"), emptySynthesizer());

		expect(() => service.start({ ...request(audioSource), signal: controller.signal })).toThrow(reason);
		expect(audioTouched).toBe(false);
	});

	it("captures constructor ports and host limits exactly once", () => {
		const optionReads = new Map<PropertyKey, number>();
		const runtimeReads = new Map<PropertyKey, number>();
		const limitReads = new Map<PropertyKey, number>();
		let recognizeReads = 0;
		let synthesizeReads = 0;
		const count = (reads: Map<PropertyKey, number>, property: PropertyKey) =>
			reads.set(property, (reads.get(property) ?? 0) + 1);
		const runtime = new Proxy(undefinedRuntime(), {
			get(target, property, receiver) {
				count(runtimeReads, property);
				return Reflect.get(target, property, receiver);
			},
		});
		const recognizer = {
			get recognize() {
				recognizeReads += 1;
				return finalRecognizer("unused").recognize;
			},
		};
		const synthesizer = {
			get synthesize() {
				synthesizeReads += 1;
				return emptySynthesizer().synthesize;
			},
		};
		const limits = new Proxy(LIMITS, {
			get(target, property, receiver) {
				count(limitReads, property);
				return Reflect.get(target, property, receiver);
			},
		});
		const options = new Proxy(
			{
				runtime,
				recognizer,
				synthesizer,
				projectInput: () => ({ inputId: "input", payload: {} }),
				limits,
			},
			{
				get(target, property, receiver) {
					count(optionReads, property);
					return Reflect.get(target, property, receiver);
				},
			},
		);

		expect(() => new GameRealtimeVoiceService(options)).not.toThrow();
		expect(Object.fromEntries(optionReads)).toEqual({
			runtime: 1,
			recognizer: 1,
			synthesizer: 1,
			projectInput: 1,
			limits: 1,
			health: 1,
		});
		expect(Object.fromEntries(runtimeReads)).toEqual({ start: 1, abortRun: 1, subscribeImmediate: 1 });
		expect({ recognizeReads, synthesizeReads }).toEqual({ recognizeReads: 1, synthesizeReads: 1 });
		expect([...limitReads.values()]).toEqual(Object.keys(LIMITS).map(() => 1));
	});

	it("captures changing passive health fields once before validating dependency identities", () => {
		let healthReads = 0;
		let nowReads = 0;
		let recognizerReads = 0;
		let synthesizerReads = 0;
		const dependency = (id: string) => ({ id, requirement: "required" as const, maxObservationAgeMs: 10 });
		const health = {
			get now() {
				nowReads += 1;
				return () => 1;
			},
			get recognizer() {
				recognizerReads += 1;
				return dependency(recognizerReads === 1 ? "recognizer" : "duplicate");
			},
			get synthesizer() {
				synthesizerReads += 1;
				return dependency(synthesizerReads === 1 ? "synthesizer" : "duplicate");
			},
		};
		const options = {
			runtime: undefinedRuntime(),
			recognizer: finalRecognizer("unused"),
			synthesizer: emptySynthesizer(),
			projectInput: () => ({ inputId: "input", payload: {} }),
			limits: LIMITS,
			get health() {
				healthReads += 1;
				return health;
			},
		};

		const service = new GameRealtimeVoiceService(options);
		expect({ healthReads, nowReads, recognizerReads, synthesizerReads }).toEqual({
			healthReads: 1,
			nowReads: 1,
			recognizerReads: 1,
			synthesizerReads: 1,
		});
		expect(service.healthDependencies().map(({ id }) => id)).toEqual(["recognizer", "synthesizer"]);
		expect(
			() =>
				new RuntimeHealthMonitor({
					dependencies: service.healthDependencies(),
					limits: {
						maxDependencies: 2,
						maxDependencyIdBytes: 64,
						maxConcurrentInspections: 2,
						inspectionTimeoutMs: 1_000,
					},
				}),
		).not.toThrow();
	});

	it("fails closed when the consumer exceeds the explicit event backlog", async () => {
		const runtime = await createRuntime(() => textResponse("unused"));
		const service = createService(runtime, finalRecognizer("Speak", ["S", "Sp"]), emptySynthesizer(), {
			...LIMITS,
			maxEventBacklog: 1,
		});
		const turn = service.start(request(audio(bytes([1]))));

		await expect(turn.completion).resolves.toMatchObject({ status: "failed", failure: "event_backpressure" });
		expect((await collect(turn.events)).length).toBe(1);
	});

	it("fails closed when streamed text outruns the explicit synthesizer backlog", async () => {
		const runtime = await createRuntime(() => textResponse("AB", ["A", "B"]));
		const service = createService(
			runtime,
			finalRecognizer("Speak"),
			{
				synthesize() {
					return neverEndingAudio();
				},
			},
			{ ...LIMITS, maxPendingTextSegments: 1 },
		);
		const turn = service.start(request(audio(bytes([1]))));

		await collect(turn.events);
		await expect(turn.completion).resolves.toMatchObject({ status: "failed", failure: "text_backpressure" });
	});

	it("releases the voice channel after text backpressure and drops the stalled provider tail", async () => {
		let response = 0;
		const runtime = await createRuntime(() =>
			response++ === 0 ? textResponse("AB", ["A", "B"]) : textResponse("Recovered"),
		);
		let synthesis = 0;
		let returnCalls = 0;
		let resolveLateChunk: ((result: IteratorResult<RealtimeAudioChunk>) => void) | undefined;
		let resolveRecoveryStarted: (() => void) | undefined;
		let releaseRecovery: (() => void) | undefined;
		const recoveryStarted = new Promise<void>((resolve) => {
			resolveRecoveryStarted = resolve;
		});
		const recoveryGate = new Promise<void>((resolve) => {
			releaseRecovery = resolve;
		});
		const service = createService(
			runtime,
			finalRecognizer("Speak"),
			{
				synthesize(request) {
					if (synthesis++ > 0) {
						return (async function* () {
							resolveRecoveryStarted?.();
							await recoveryGate;
							for await (const _text of request.text) {
								// consume
							}
							yield { bytes: bytes([7]) };
						})();
					}
					return {
						[Symbol.asyncIterator]() {
							return {
								next: () =>
									new Promise<IteratorResult<RealtimeAudioChunk>>((resolve) => {
										resolveLateChunk = resolve;
									}),
								return: async () => {
									returnCalls += 1;
									return { done: true as const, value: undefined };
								},
							};
						},
					};
				},
			},
			{ ...LIMITS, maxPendingTextSegments: 1 },
		);
		const failed = service.start(request(audio(bytes([1]))));
		const failedEvents = collect(failed.events);

		await expect(failed.completion).resolves.toMatchObject({ status: "failed", failure: "text_backpressure" });
		expect(resolveLateChunk).toBeTypeOf("function");
		if (!resolveLateChunk) throw new Error("stalled synthesizer did not request output");
		const releaseLateChunk = resolveLateChunk;

		const recovered = service.start({
			...request(audio(bytes([2]))),
			coordinate: { ...COORDINATE, epoch: "2", utteranceId: "utterance-2" },
		});
		const recoveredEvents = collect(recovered.events);
		await recoveryStarted;
		releaseLateChunk({ done: false, value: { bytes: bytes([9]) } });
		releaseRecovery?.();
		await expect(recovered.completion).resolves.toMatchObject({ status: "completed" });
		expect((await failedEvents).some(isAudioChunk)).toBe(false);
		const recoveredAudio = (await recoveredEvents).filter(isAudioChunk).map((event) => [...event.chunk.bytes]);
		expect(recoveredAudio).toEqual([[7]]);
		expect(recoveredAudio).not.toContainEqual([9]);
		expect({ returnCalls, synthesis }).toEqual({ returnCalls: 1, synthesis: 2 });
	});

	it.each(["closeAuthority", "abortRun", "providerFailure"] as const)(
		"cancels stalled TTS after external %s, reuses the channel, and drops old audio",
		async (operation) => {
			let providerCalls = 0;
			let providerSignal: AbortSignal | undefined;
			let failProvider: (() => void) | undefined;
			const runtime = await createRuntime((_model, _context, options) => {
				if (providerCalls++ > 0) return textResponse("Recovered");
				providerSignal = options?.signal;
				const stream = createAssistantMessageEventStream();
				const partial = assistant([{ type: "text", text: "Speaking" }], "pending");
				queueMicrotask(() => {
					stream.push({ type: "start", partial: assistant([], "pending") });
					stream.push({
						type: "text_start",
						contentIndex: 0,
						partial: assistant([{ type: "text", text: "" }], "pending"),
					});
					stream.push({ type: "text_delta", contentIndex: 0, delta: "Speaking", partial });
				});
				options?.signal?.addEventListener(
					"abort",
					() => {
						stream.push({
							type: "error",
							reason: "aborted",
							error: assistant([{ type: "text", text: "Speaking" }], "aborted"),
						});
					},
					{ once: true },
				);
				failProvider = () =>
					stream.push({
						type: "error",
						reason: "error",
						error: {
							...assistant([{ type: "text", text: "Speaking" }], "error"),
							errorMessage: "provider unavailable",
						},
					});
				return stream;
			});
			const synthesisStarted = Promise.withResolvers<void>();
			const recoveryStarted = Promise.withResolvers<void>();
			const lateChunk = Promise.withResolvers<IteratorResult<RealtimeAudioChunk>>();
			let synthesisSignal: AbortSignal | undefined;
			let synthesisCalls = 0;
			let returnCalls = 0;
			let generation = "1";
			const service = new GameRealtimeVoiceService({
				runtime,
				recognizer: finalRecognizer("Continue"),
				synthesizer: {
					synthesize(request) {
						if (synthesisCalls++ > 0) {
							recoveryStarted.resolve();
							return oneChunkSynthesizer().synthesize(request);
						}
						synthesisSignal = request.signal;
						return {
							[Symbol.asyncIterator]() {
								return {
									next() {
										synthesisStarted.resolve();
										return lateChunk.promise;
									},
									return: async () => {
										returnCalls += 1;
										return { done: true as const, value: undefined };
									},
								};
							},
						};
					},
				},
				limits: LIMITS,
				projectInput: () => ({
					inputId: `voice-planning-${generation}`,
					payload: { instruction: "Continue" },
					admissionAuthority: { scope: "planning", generation },
				}),
			});
			const turn = service.start(request(audio(bytes([1]))));
			const events = collect(turn.events);
			const recoveredCoordinate = { ...COORDINATE, epoch: "2", utteranceId: "utterance-2" };
			try {
				await synthesisStarted.promise;
				if (operation === "closeAuthority") {
					await runtime.closeAuthority({
						...STREAM,
						scope: "planning",
						generation: "1",
						operationId: "close-speaking",
					});
				} else if (operation === "abortRun") {
					const coordinate = runtime.readActiveCoordinate(STREAM);
					if (!coordinate) throw new Error("speaking runtime did not remain active");
					await expect(runtime.abortRun(coordinate, new Error("host stopped the run"))).resolves.toMatchObject({
						accepted: true,
					});
				} else {
					if (!failProvider) throw new Error("speaking provider did not start");
					failProvider();
				}
				if (operation !== "providerFailure") expect(providerSignal?.aborted).toBe(true);
				await expect.poll(() => synthesisSignal?.aborted).toBe(true);
				await expect(turn.completion).resolves.toMatchObject(
					operation === "providerFailure"
						? { status: "failed", agentStatus: "failed", failure: "agent" }
						: { status: "interrupted", agentStatus: "aborted" },
				);

				generation = "2";
				const recovered = service.start({ ...request(audio(bytes([2]))), coordinate: recoveredCoordinate });
				const recoveredEvents = collect(recovered.events);
				await recoveryStarted.promise;
				lateChunk.resolve({ done: false, value: { bytes: bytes([9]) } });
				await expect(recovered.completion).resolves.toMatchObject({ status: "completed" });
				expect((await events).some(isAudioChunk)).toBe(false);
				expect((await recoveredEvents).filter(isAudioChunk).map((event) => [...event.chunk.bytes])).toEqual([[1]]);
				expect({ synthesisCalls, returnCalls }).toEqual({ synthesisCalls: 2, returnCalls: 1 });
			} finally {
				await service.interrupt(COORDINATE, new Error("test cleanup"));
				await service.interrupt(recoveredCoordinate, new Error("test cleanup"));
				lateChunk.resolve({ done: true, value: undefined });
				await turn.completion;
				await events;
			}
		},
	);

	it("drops a late synthesizer chunk after an exact interruption", async () => {
		const runtime = await createRuntime(() => textResponse("Speaking"));
		let resolveStarted: (() => void) | undefined;
		let resolveNext: ((result: IteratorResult<RealtimeAudioChunk>) => void) | undefined;
		const started = new Promise<void>((resolve) => {
			resolveStarted = resolve;
		});
		const pending = new Promise<IteratorResult<RealtimeAudioChunk>>((resolve) => {
			resolveNext = resolve;
		});
		const service = createService(runtime, finalRecognizer("Continue"), {
			synthesize() {
				return {
					[Symbol.asyncIterator]() {
						return {
							next() {
								resolveStarted?.();
								return pending;
							},
							return: async () => ({ done: true as const, value: undefined }),
						};
					},
				};
			},
		});
		const turn = service.start(request(audio(bytes([1]))));
		const eventsPromise = collect(turn.events);
		await started;

		await service.interrupt(COORDINATE, new Error("barge in"));
		resolveNext?.({ done: false, value: { bytes: bytes([9]) } });
		await expect(turn.completion).resolves.toMatchObject({ status: "interrupted" });
		expect((await eventsPromise).some((event) => event.type.startsWith("assistant.audio"))).toBe(false);
	});

	it("does not report success when the terminal event cannot enter the backlog", async () => {
		const runtime = await createRuntime(() => terminalResponse(assistant([], "stop")));
		const service = createService(runtime, finalRecognizer("Wait"), emptySynthesizer(), {
			...LIMITS,
			maxEventBacklog: 2,
		});
		const turn = service.start(request(audio(bytes([1]))));

		await expect(turn.completion).resolves.toMatchObject({ status: "failed", failure: "event_backpressure" });
		expect((await collect(turn.events)).map((event) => event.type)).toEqual([
			"input.transcript.final",
			"agent.started",
		]);
	});

	it("admits audio bytes before copying and rejects hostile or over-budget chunks", async () => {
		const runtime = await createRuntime(() => textResponse("unused"));
		let recognitions = 0;
		const service = createService(
			runtime,
			{
				async *recognize(request) {
					recognitions += 1;
					for await (const _chunk of request.audio) {
						// consume
					}
					yield { type: "transcript.final", text: "unused" };
				},
			},
			emptySynthesizer(),
			{ ...LIMITS, maxInputChunkBytes: 1, maxInputAudioBytes: 1 },
		);
		const oversized = service.start(request(audio(bytes([1, 2]))));

		await collect(oversized.events);
		await expect(oversized.completion).resolves.toMatchObject({ status: "failed", failure: "input_capacity" });
		expect(recognitions).toBe(1);

		const proxied = service.start(request(audio(new Proxy(bytes([1]), {}))));
		await collect(proxied.events);
		await expect(proxied.completion).resolves.toMatchObject({ status: "failed", failure: "invalid_audio" });
	});

	it("classifies asynchronous recognition protocol failures without reading iterator hooks twice", async () => {
		const runtime = await createRuntime(() => textResponse("unused"));
		let nextReads = 0;
		let returnReads = 0;
		const recognizer: RealtimeSpeechRecognizer = {
			recognize() {
				return {
					[Symbol.asyncIterator]() {
						return {
							get next() {
								nextReads += 1;
								return () => Promise.reject(new Error("STT offline"));
							},
							get return() {
								returnReads += 1;
								return async () => ({ done: true as const, value: undefined });
							},
						};
					},
				};
			},
		};
		const service = createService(runtime, recognizer, emptySynthesizer());
		const rejected = service.start(request(audio(bytes([1]))));

		await collect(rejected.events);
		await expect(rejected.completion).resolves.toMatchObject({ status: "failed", failure: "recognition" });
		expect({ nextReads, returnReads }).toEqual({ nextReads: 1, returnReads: 1 });

		const malformed = createService(
			runtime,
			{
				recognize() {
					return {
						[Symbol.asyncIterator]() {
							return {
								next: async () =>
									Object.defineProperty({}, "done", {
										get() {
											throw new Error("malformed iterator result");
										},
									}) as IteratorResult<never>,
							};
						},
					};
				},
			},
			emptySynthesizer(),
		);
		const malformedTurn = malformed.start(request(audio(bytes([1]))));
		await collect(malformedTurn.events);
		await expect(malformedTurn.completion).resolves.toMatchObject({ status: "failed", failure: "recognition" });
	});

	it("captures the source audio next and return hooks once and closes the iterator once", async () => {
		const runtime = await createRuntime(() => textResponse("Done"));
		let nextReads = 0;
		let returnReads = 0;
		let returnCalls = 0;
		let index = 0;
		const source = {
			[Symbol.asyncIterator]() {
				return {
					get next() {
						nextReads += 1;
						return async () =>
							index++ === 0
								? ({ value: { bytes: bytes([1]) } } as IteratorYieldResult<RealtimeAudioChunk>)
								: { done: true as const, value: undefined };
					},
					get return() {
						returnReads += 1;
						return async () => {
							returnCalls += 1;
							return { done: true as const, value: undefined };
						};
					},
				};
			},
		};
		const service = createService(runtime, finalRecognizer("Continue"), oneChunkSynthesizer());
		const turn = service.start(request(source));

		await collect(turn.events);
		await expect(turn.completion).resolves.toMatchObject({ status: "completed" });
		expect({ nextReads, returnReads, returnCalls }).toEqual({ nextReads: 1, returnReads: 1, returnCalls: 1 });
	});
});

function createService(
	runtime: GameRuntime,
	recognizer: RealtimeSpeechRecognizer,
	synthesizer: RealtimeSpeechSynthesizer,
	limits: RealtimeVoiceLimits = LIMITS,
	health?: RealtimeVoiceHealthOptions,
): GameRealtimeVoiceService {
	return new GameRealtimeVoiceService({
		runtime,
		recognizer,
		synthesizer,
		limits,
		...(health ? { health } : {}),
		projectInput: ({ coordinate, transcript }) => ({
			inputId: `voice:${coordinate.voiceSessionId}:${coordinate.epoch}:${coordinate.utteranceId}`,
			payload: { instruction: transcript },
		}),
	});
}

async function createRuntime(
	modelStream: StreamFn,
	options: {
		readonly toolSource?: () => readonly AgentTool[];
		readonly newRunId?: () => string;
		readonly eventStore?: InMemoryEventStore;
	} = {},
): Promise<GameRuntime> {
	let eventId = 0;
	let agentId = 0;
	const runtime = new GameRuntime(
		options.eventStore ??
			new InMemoryEventStore({
				newEventId: () => `event-${++eventId}`,
				now: () => "2026-08-31T00:00:00.000Z",
			}),
		{
			maxActiveActors: 1,
			maxQueuedRuns: 4,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			...(options.newRunId === undefined ? {} : { newRunId: options.newRunId }),
		},
	);
	await runtime.registerActor({
		...STREAM,
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

function request(audioInput: AsyncIterable<RealtimeAudioChunk>) {
	return {
		coordinate: COORDINATE,
		inputFormat: INPUT_FORMAT,
		outputFormat: OUTPUT_FORMAT,
		voiceProfileId: "guard-voice",
		audio: audioInput,
	} as const;
}

function finalRecognizer(text: string, deltas: readonly string[] = []): RealtimeSpeechRecognizer {
	return {
		async *recognize(request) {
			for await (const _chunk of request.audio) {
				// consume
			}
			for (const delta of deltas) yield { type: "transcript.delta" as const, text: delta };
			yield { type: "transcript.final", text };
		},
	};
}

function emptySynthesizer(): RealtimeSpeechSynthesizer {
	return {
		async *synthesize(request) {
			for await (const _text of request.text) {
				// consume
			}
			yield* [] as RealtimeAudioChunk[];
		},
	};
}

function oneChunkSynthesizer(): RealtimeSpeechSynthesizer {
	return {
		async *synthesize(request) {
			for await (const _text of request.text) {
				// consume
			}
			yield { bytes: bytes([1]) };
		},
	};
}

async function* audio(...chunks: Uint8Array[]): AsyncIterable<RealtimeAudioChunk> {
	for (const chunk of chunks) yield { bytes: chunk };
}

function bytes(values: readonly number[]): Uint8Array {
	return Uint8Array.from(values);
}

async function collect(events: AsyncIterable<Readonly<RealtimeVoiceEvent>>): Promise<Readonly<RealtimeVoiceEvent>[]> {
	const collected: Readonly<RealtimeVoiceEvent>[] = [];
	for await (const event of events) collected.push(event);
	return collected;
}

function isAudioChunk(
	event: Readonly<RealtimeVoiceEvent>,
): event is Extract<RealtimeVoiceEvent, { type: "assistant.audio.chunk" }> {
	return event.type === "assistant.audio.chunk";
}

function terminalResponse(message: AssistantMessage) {
	const stream = createAssistantMessageEventStream();
	queueMicrotask(() => {
		stream.push({ type: "start", partial: { ...message, content: [], stopReason: "pending" } });
		stream.push({
			type: "done",
			reason: message.stopReason as "stop" | "length" | "toolUse" | "deferred",
			message,
		});
	});
	return stream;
}

function finalTextWithoutDelta(text: string) {
	const message = assistant([{ type: "text", text }], "stop");
	const stream = createAssistantMessageEventStream();
	queueMicrotask(() => {
		stream.push({ type: "start", partial: { ...message, content: [], stopReason: "pending" } });
		stream.push({
			type: "text_start",
			contentIndex: 0,
			partial: { ...message, content: [{ type: "text", text: "" }], stopReason: "pending" },
		});
		stream.push({ type: "text_end", contentIndex: 0, content: text, partial: message });
		stream.push({ type: "done", reason: "stop", message });
	});
	return stream;
}

function inconsistentFinalText(delta: string) {
	const stream = createAssistantMessageEventStream();
	queueMicrotask(() => {
		stream.push({ type: "start", partial: assistant([], "pending") });
		stream.push({
			type: "text_start",
			contentIndex: 0,
			partial: assistant([{ type: "text", text: "" }], "pending"),
		});
		stream.push({
			type: "text_delta",
			contentIndex: 0,
			delta,
			partial: assistant([{ type: "text", text: delta }], "pending"),
		});
		stream.push({ type: "done", reason: "stop", message: assistant([], "stop") });
	});
	return stream;
}

function neverEndingEvents(): AsyncIterable<never> {
	return {
		[Symbol.asyncIterator]() {
			return {
				next: () => new Promise<IteratorResult<never>>(() => undefined),
			};
		},
	};
}

function neverEndingAudio(): AsyncIterable<RealtimeAudioChunk> {
	return {
		[Symbol.asyncIterator]() {
			return {
				next: () => new Promise<IteratorResult<RealtimeAudioChunk>>(() => undefined),
			};
		},
	};
}

function stallingRuntime(): {
	readonly runtime: GameRuntime;
	readonly firstStarted: Promise<void>;
	releaseFirst(): void;
} {
	let startCount = 0;
	let resolveFirstStarted: (() => void) | undefined;
	let resolveFirst: ((result: { runId: string; status: "aborted" }) => void) | undefined;
	const firstStarted = new Promise<void>((resolve) => {
		resolveFirstStarted = resolve;
	});
	const firstCompletion = new Promise<{ runId: string; status: "aborted" }>((resolve) => {
		resolveFirst = resolve;
	});
	const runtime = {
		start(input: { sessionId: string; actorId: string }) {
			startCount += 1;
			const runId = `run-${startCount}`;
			const initialCoordinate = { ...input, runId, turn: "0" };
			if (startCount === 1) resolveFirstStarted?.();
			return {
				runId,
				initialCoordinate,
				completion: startCount === 1 ? firstCompletion : Promise.resolve({ runId, status: "completed" as const }),
			};
		},
		async abortRun(expected: { sessionId: string; actorId: string; runId: string }) {
			return { accepted: true as const, coordinate: { ...expected, turn: "1" } };
		},
		subscribeImmediate: () => () => undefined,
	} as unknown as GameRuntime;
	return {
		runtime,
		firstStarted,
		releaseFirst: () => resolveFirst?.({ runId: "run-1", status: "aborted" }),
	};
}

function undefinedRuntime(): GameRuntime {
	return {
		start: () => {
			throw new Error("must not start");
		},
		abortRun: async () => ({ accepted: false, reason: "no-active-run" }),
		subscribeImmediate: () => () => undefined,
	} as unknown as GameRuntime;
}
