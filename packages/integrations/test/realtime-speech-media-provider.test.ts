import { Buffer } from "node:buffer";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
	InMemoryMediaJobStore,
	LocalGeneratedResourceStore,
	MediaJobConflictError,
	type MediaGenerationRequest,
	type MediaJobLimits,
	MediaJobService,
	MediaCallAdmissionGroup,
	type MediaProviderOperationContext,
} from "@opengameagent/media";
import { PCM_S16LE_MEDIA_TYPE } from "@opengameagent/realtime-local";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
	createRealtimeSpeechMediaProvider,
	RealtimeSpeechMediaAdapterError,
	type RealtimeSpeechMediaProviderLimits,
} from "../src/index.js";

const FORMAT = Object.freeze({
	mediaType: PCM_S16LE_MEDIA_TYPE,
	sampleRateHz: 24_000,
	channelCount: 1,
});

const LIMITS = Object.freeze({
	maxIdentifierBytes: 64,
	maxTextBytes: 128,
	maxOutputChunks: 2,
	maxOutputChunkBytes: 4,
	maxOutputAudioBytes: 8,
});

const MEDIA_LIMITS: MediaJobLimits = Object.freeze({
	maxIdentifierBytes: 64,
	maxAdapterIdBytes: 64,
	maxAdapters: 1,
	maxRequestBytes: 1_024,
	maxJsonDepth: 4,
	maxProviderHandleBytes: 64,
	maxAttemptsPerJob: 2,
	maxOutputsPerAttempt: 1,
	maxResourceBytes: 1_024,
	maxBundleBytes: 2_048,
	maxManifestBytes: 1_024,
	maxMediaTypeBytes: 64,
});

const temporaryDirectories: string[] = [];

afterEach(async () => {
	await Promise.all(temporaryDirectories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })));
});

describe("createRealtimeSpeechMediaProvider", () => {
	it.each([
		{ name: "wrong media kind", request: { kind: "image", input: { text: "hello" } } },
		{ name: "missing text", request: { kind: "audio", input: {} } },
		{ name: "empty text", request: { kind: "audio", input: { text: "" } } },
		{ name: "non-string text", request: { kind: "audio", input: { text: 1 } } },
		{ name: "extra input fields", request: { kind: "audio", input: { text: "hello", voice: "override" } } },
	] satisfies readonly { readonly name: string; readonly request: MediaGenerationRequest }[])(
		"rejects $name before synthesis",
		async ({ request }) => {
			const synthesize = vi.fn(async function* () {});
			const provider = createRealtimeSpeechMediaProvider({
				synthesizer: { synthesize },
				voiceProfileId: "manual-npc",
				outputFormat: FORMAT,
				limits: LIMITS,
			});

			await expect(provider.start(request, context())).resolves.toEqual({ status: "failed", failure: "malformed" });
			expect(synthesize).not.toHaveBeenCalled();
		},
	);

	it("rejects oversized text as capacity before synthesis", async () => {
		const synthesize = vi.fn(async function* () {});
		const provider = createRealtimeSpeechMediaProvider({
			synthesizer: { synthesize },
			voiceProfileId: "manual-npc",
			outputFormat: FORMAT,
			limits: { ...LIMITS, maxTextBytes: 3 },
		});

		await expect(provider.start({ kind: "audio", input: { text: "你好" } }, context())).resolves.toEqual({
			status: "failed",
			failure: "capacity",
		});
		expect(synthesize).not.toHaveBeenCalled();
	});

	it("preserves exact multiline text for synthesis", async () => {
		let received = "";
		const provider = createRealtimeSpeechMediaProvider({
			synthesizer: {
				async *synthesize(request) {
					for await (const text of request.text) received += text;
					yield { bytes: new Uint8Array([1, 2]) };
				},
			},
			voiceProfileId: "manual-npc",
			outputFormat: FORMAT,
			limits: LIMITS,
		});

		await expect(
			provider.start({ kind: "audio", input: { text: "第一行\n第二行" } }, context()),
		).resolves.toMatchObject({
			status: "succeeded",
		});
		expect(received).toBe("第一行\n第二行");
	});

	it("captures the media input object once before validating it", async () => {
		let reads = 0;
		let received = "";
		const provider = createRealtimeSpeechMediaProvider({
			synthesizer: {
				async *synthesize(request) {
					for await (const text of request.text) received += text;
					yield { bytes: new Uint8Array([1, 2]) };
				},
			},
			voiceProfileId: "manual-npc",
			outputFormat: FORMAT,
			limits: LIMITS,
		});
		const request = {
			kind: "audio" as const,
			get input() {
				reads += 1;
				return reads === 1 ? { text: "captured" } : { text: "changed", extra: true };
			},
		};

		await expect(provider.start(request, context())).resolves.toMatchObject({ status: "succeeded" });
		expect(reads).toBe(1);
		expect(received).toBe("captured");
	});

	it.each([
		{ name: "empty output", values: [] },
		{ name: "unaligned PCM16 output", values: [new Uint8Array([1])] },
	])("maps $name to a malformed media failure", async ({ values }) => {
		const provider = speechProvider(values);

		await expect(provider.start({ kind: "audio", input: { text: "hello" } }, context())).resolves.toEqual({
			status: "failed",
			failure: "malformed",
		});
	});

	it.each([
		{
			name: "chunk count",
			values: [new Uint8Array([1, 2]), new Uint8Array([3, 4]), new Uint8Array([5, 6])],
			limits: LIMITS,
		},
		{ name: "per-chunk bytes", values: [new Uint8Array(6)], limits: LIMITS },
		{
			name: "aggregate bytes",
			values: [new Uint8Array(4), new Uint8Array(4)],
			limits: { ...LIMITS, maxOutputAudioBytes: 6 },
		},
	])("maps $name overflow to a capacity media failure", async ({ values, limits }) => {
		const provider = speechProvider(values, limits);

		await expect(provider.start({ kind: "audio", input: { text: "hello" } }, context())).resolves.toEqual({
			status: "failed",
			failure: "capacity",
		});
	});

	it("redacts an unprovable synthesizer failure without claiming a terminal result", async () => {
		const hostile = new Proxy(
			{},
			{
				getPrototypeOf() {
					throw new Error("secret provider detail");
				},
			},
		);
		const provider = createRealtimeSpeechMediaProvider({
			synthesizer: {
				synthesize: () => ({
					[Symbol.asyncIterator]() {
						return {
							async next() {
								throw hostile;
							},
						};
					},
				}),
			},
			voiceProfileId: "manual-npc",
			outputFormat: FORMAT,
			limits: LIMITS,
		});

		let failure: unknown;
		try {
			await provider.start({ kind: "audio", input: { text: "hello" } }, context());
		} catch (error) {
			failure = error;
		}
		expect(failure).toMatchObject({
			name: "RealtimeSpeechMediaAdapterError",
			message: "provider_outcome_unknown",
		});
		expect(JSON.stringify(failure)).not.toContain("secret provider detail");
	});

	it("preserves cancellation that occurs while capturing untrusted media input", async () => {
		const controller = new AbortController();
		const reason = new Error("cancel-during-media-input");
		const synthesize = vi.fn(async function* () {});
		const provider = createRealtimeSpeechMediaProvider({
			synthesizer: { synthesize },
			voiceProfileId: "manual-npc",
			outputFormat: FORMAT,
			limits: LIMITS,
		});
		const request = {
			kind: "audio" as const,
			get input(): never {
				controller.abort(reason);
				throw new Error("hostile input getter");
			},
		};

		await expect(provider.start(request, context(controller.signal))).rejects.toBe(reason);
		expect(synthesize).not.toHaveBeenCalled();
	});

	it("preserves cancellation, closes pending output once and ignores late audio", async () => {
		const controller = new AbortController();
		const reason = new Error("cancel-manual-speech");
		let settle: ((result: IteratorResult<{ bytes: Uint8Array }>) => void) | undefined;
		const close = vi.fn(async () => ({ done: true as const, value: undefined }));
		const provider = createRealtimeSpeechMediaProvider({
			synthesizer: {
				synthesize: () => ({
					[Symbol.asyncIterator]() {
						return {
							next: () =>
								new Promise<IteratorResult<{ bytes: Uint8Array }>>((resolve) => {
									settle = resolve;
									controller.abort(reason);
								}),
							return: close,
						};
					},
				}),
			},
			voiceProfileId: "manual-npc",
			outputFormat: FORMAT,
			limits: LIMITS,
		});

		await expect(provider.start({ kind: "audio", input: { text: "hello" } }, context(controller.signal))).rejects.toBe(
			reason,
		);
		expect(close).toHaveBeenCalledOnce();
		settle?.({ done: false, value: { bytes: new Uint8Array([1, 2]) } });
		await Promise.resolve();
		expect(close).toHaveBeenCalledOnce();
	});

	it("rejects output formats that cannot be represented by a PCM WAV header", () => {
		expect(() =>
			createRealtimeSpeechMediaProvider({
				synthesizer: { synthesize: async function* () {} },
				voiceProfileId: "manual-npc",
				outputFormat: { ...FORMAT, sampleRateHz: 0x1_0000_0000 },
				limits: LIMITS,
			}),
		).toThrow(RealtimeSpeechMediaAdapterError);
		expect(() =>
			createRealtimeSpeechMediaProvider({
				synthesizer: { synthesize: async function* () {} },
				voiceProfileId: "manual-npc",
				outputFormat: { ...FORMAT, channelCount: 0x1_0000 },
				limits: LIMITS,
			}),
		).toThrow(RealtimeSpeechMediaAdapterError);
		expect(() =>
			createRealtimeSpeechMediaProvider({
				synthesizer: { synthesize: async function* () {} },
				voiceProfileId: "manual-npc",
				outputFormat: FORMAT,
				limits: { ...LIMITS, maxOutputAudioBytes: 0xffff_ffff - 35 },
			}),
		).toThrow(RealtimeSpeechMediaAdapterError);
	});

	it("turns one trusted text input into one PCM16 WAV media resource", async () => {
		let receivedText = "";
		const provider = createRealtimeSpeechMediaProvider({
			synthesizer: {
				async *synthesize(request) {
					for await (const segment of request.text) receivedText += segment;
					yield { bytes: new Uint8Array([1, 2]) };
					yield { bytes: new Uint8Array([3, 4]) };
				},
			},
			voiceProfileId: "manual-npc",
			outputFormat: FORMAT,
			limits: LIMITS,
		});

		const observation = await provider.start({ kind: "audio", input: { text: "你好" } }, context());

		expect(receivedText).toBe("你好");
		expect(observation.status).toBe("succeeded");
		if (observation.status !== "succeeded") throw new Error("expected succeeded observation");
		expect(observation.resources).toHaveLength(1);
		const resource = observation.resources[0];
		if (!resource) throw new Error("expected WAV resource");
		expect(resource).toMatchObject({ ordinal: 0, kind: "audio", mediaType: "audio/wav", declaredBytes: 48 });
		expect(new TextDecoder().decode((resource.bytes as Uint8Array).subarray(0, 4))).toBe("RIFF");
		expect([...(resource.bytes as Uint8Array).subarray(44)]).toEqual([1, 2, 3, 4]);
	});

	it("persists one WAV bundle and rejects duplicate jobs without resynthesis", async () => {
		const synthesize = vi.fn(async function* () {
			yield { bytes: new Uint8Array([1, 2]) };
			yield { bytes: new Uint8Array([3, 4]) };
		});
		const provider = createRealtimeSpeechMediaProvider({
			synthesizer: { synthesize },
			voiceProfileId: "manual-npc",
			outputFormat: FORMAT,
			limits: LIMITS,
		});
		const root = await temporaryDirectory("oga-realtime-speech-media-");
		const resources = await LocalGeneratedResourceStore.open({ rootDirectory: join(root, "resources") });
		const store = new InMemoryMediaJobStore(MEDIA_LIMITS);
		const service = new MediaJobService({
			adapters: [
				{
					adapterId: "manual-speech",
					provider,
					admission: new MediaCallAdmissionGroup({ maxConcurrentCalls: 1, maxQueuedCalls: 1 }),
				},
			],
			store,
			resources,
			limits: MEDIA_LIMITS,
			newId: idFactory(),
			now: () => 1_800_000_000_000,
		});

		const completed = await service.submit("manual-speech", {
			jobId: "npc-line-1",
			kind: "audio",
			input: { text: "你好" },
		});

		expect(completed).toMatchObject({ status: "succeeded", bundleId: "bundle-1" });
		const bundle = await service.readBundle("npc-line-1", "bundle-1");
		const resource = bundle?.resources[0];
		expect(resource).toMatchObject({
			ordinal: 0,
			kind: "audio",
			mediaType: "audio/wav",
			byteLength: 48,
			sha256: "49a0cf1fde14e03bfc12de93bb5ffd76d8d754a5492a0bbc4b6d654c4183d5a9",
		});
		if (!resource) throw new Error("expected persisted WAV resource");
		await expect(
			service.selectResource({
				jobId: "npc-line-1",
				attemptId: "attempt-1",
				bundleId: "bundle-1",
				ordinal: 0,
				resourceId: resource.resourceId,
			}),
		).resolves.toMatchObject({ bundleDisposition: "complete", resource });
		const bytes = await resources.read(resource, {
			maxBytes: 48,
			maxMediaTypeBytes: 64,
			signal: new AbortController().signal,
		});
		expect(Buffer.from(bytes).toString("hex")).toBe(
			"524946462800000057415645666d74201000000001000100c05d000080bb000002001000646174610400000001020304",
		);
		await expect(
			service.submit("manual-speech", { jobId: "npc-line-1", kind: "audio", input: { text: "different" } }),
		).rejects.toBeInstanceOf(MediaJobConflictError);
		expect(synthesize).toHaveBeenCalledOnce();
	});

	it("keeps an unprovable synthesis outcome unknown until explicit abandon and restart", async () => {
		const synthesize = vi.fn(async function* () {
			if (synthesize.mock.calls.length === 1) throw new Error("private provider failure");
			yield { bytes: new Uint8Array([1, 2]) };
		});
		const provider = createRealtimeSpeechMediaProvider({
			synthesizer: { synthesize },
			voiceProfileId: "manual-npc",
			outputFormat: FORMAT,
			limits: LIMITS,
		});
		const root = await temporaryDirectory("oga-realtime-speech-restart-");
		const resources = await LocalGeneratedResourceStore.open({ rootDirectory: join(root, "resources") });
		const store = new InMemoryMediaJobStore(MEDIA_LIMITS);
		const service = new MediaJobService({
			adapters: [
				{
					adapterId: "manual-speech",
					provider,
					admission: new MediaCallAdmissionGroup({ maxConcurrentCalls: 1, maxQueuedCalls: 1 }),
				},
			],
			store,
			resources,
			limits: MEDIA_LIMITS,
			newId: idFactory(),
			now: () => 1_800_000_000_000,
		});

		const unknown = await service.submit("manual-speech", {
			jobId: "npc-line-unknown",
			kind: "audio",
			input: { text: "你好" },
		});
		expect(unknown).toMatchObject({ status: "outcome_unknown", failure: "unknown", disposition: "active" });
		expect(JSON.stringify(unknown)).not.toContain("private provider failure");
		await expect(
			service.restart({ jobId: "npc-line-unknown", expectedRevision: unknown.revision }),
		).rejects.toBeInstanceOf(MediaJobConflictError);
		const abandoned = await service.cancel({ jobId: "npc-line-unknown", expectedRevision: unknown.revision });
		const completed = await service.restart({ jobId: "npc-line-unknown", expectedRevision: abandoned.revision });

		expect(completed).toMatchObject({ status: "succeeded", currentAttemptId: "attempt-2" });
		expect(completed.attempts[0]).toMatchObject({ status: "outcome_unknown", disposition: "abandoned" });
		expect(synthesize).toHaveBeenCalledTimes(2);
	});
});

function speechProvider(values: readonly Uint8Array[], limits: Readonly<RealtimeSpeechMediaProviderLimits> = LIMITS) {
	return createRealtimeSpeechMediaProvider({
		synthesizer: {
			async *synthesize() {
				for (const bytes of values) yield { bytes };
			},
		},
		voiceProfileId: "manual-npc",
		outputFormat: FORMAT,
		limits,
	});
}

function context(signal = new AbortController().signal): MediaProviderOperationContext {
	return {
		jobId: "job-1",
		attemptId: "attempt-1",
		submissionKey: "submission-1",
		signal,
	};
}

async function temporaryDirectory(prefix: string): Promise<string> {
	const directory = await mkdtemp(join(tmpdir(), prefix));
	temporaryDirectories.push(directory);
	return directory;
}

function idFactory() {
	const counts = { attempt: 0, submission: 0, bundle: 0 };
	return (kind: keyof typeof counts) => `${kind}-${++counts[kind]}`;
}
