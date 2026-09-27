import { createHash } from "node:crypto";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";

const ort = vi.hoisted(() => {
	const tensorState = {
		created: [] as Array<{ disposed: number }>,
		failAtCreation: undefined as number | undefined,
		creations: 0,
	};
	return {
		create: vi.fn(),
		tensorState,
		Tensor: class FakeTensor {
			readonly location = "cpu";
			readonly texture = undefined;
			readonly gpuBuffer = undefined;
			readonly mlTensor = undefined;
			disposed = 0;
			constructor(
				readonly type: string,
				readonly data: Float32Array | BigInt64Array,
				readonly dims: readonly number[],
			) {
				tensorState.creations += 1;
				if (tensorState.failAtCreation === tensorState.creations) throw new Error("sensitive tensor detail");
				tensorState.created.push(this);
			}
			dispose() {
				this.disposed += 1;
			}
			async getData() {
				return this.data;
			}
		},
	};
});

vi.mock("onnxruntime-web", () => ({
	InferenceSession: { create: ort.create },
	Tensor: ort.Tensor,
}));

type FakeTensor = InstanceType<typeof ort.Tensor>;
type FakeFeeds = Record<string, FakeTensor> & {
	readonly input: FakeTensor;
	readonly state: FakeTensor;
	readonly sr: FakeTensor;
};

import { LocalSpeechProtocolError, SileroVoiceActivityDetector, SileroVoiceActivityError } from "../src/index.js";

const directories: string[] = [];

afterEach(async () => {
	vi.useRealTimers();
	ort.create.mockReset();
	ort.tensorState.created.length = 0;
	ort.tensorState.creations = 0;
	ort.tensorState.failAtCreation = undefined;
	await Promise.all(directories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })));
});

describe("SileroVoiceActivityDetector", () => {
	it("preserves the host's exact cancellation reason before model loading", async () => {
		const artifact = await modelArtifact(Uint8Array.of(9));
		const detector = new SileroVoiceActivityDetector(options(artifact));
		const controller = new AbortController();
		const reason = new Error("microphone lease revoked");
		controller.abort(reason);

		await expect(detector.open({ format: inputFormat(), signal: controller.signal })).rejects.toBe(reason);
		expect(ort.create).not.toHaveBeenCalled();
	});

	it("rejects a model whose bytes do not match the host-pinned SHA-256 before ONNX loading", async () => {
		const artifact = await modelArtifact(Uint8Array.of(1, 2, 3));
		const detector = new SileroVoiceActivityDetector({
			model: { path: artifact.path, sha256: "0".repeat(64) },
			maxModelPathBytes: 1024,
			maxModelBytes: 1024,
			modelLoadTimeoutMs: 1000,
			inferenceTimeoutMs: 1000,
			maxConcurrentSessions: 1,
		});

		await expect(
			detector.open({
				format: { mediaType: "audio/pcm;format=s16le", sampleRateHz: 16_000, channelCount: 1 },
				signal: new AbortController().signal,
			}),
		).rejects.toEqual(new SileroVoiceActivityError("invalid_artifact"));
		expect(ort.create).not.toHaveBeenCalled();
	});

	it("feeds exact PCM16 frames while carrying recurrent state and context only within one stream", async () => {
		const artifact = await modelArtifact(Uint8Array.of(4, 5, 6));
		const runs: FakeFeeds[] = [];
		let inference = 0;
		ort.create.mockResolvedValue(
			modelSession(async (feeds) => {
				runs.push(feeds);
				inference += 1;
				return {
					output: new ort.Tensor("float32", Float32Array.of(inference === 1 ? 0.75 : 0.25), [1, 1]),
					stateN: new ort.Tensor("float32", new Float32Array(256).fill(inference * 3), [2, 1, 128]),
				};
			}),
		);
		const detector = new SileroVoiceActivityDetector(options(artifact));
		const signal = new AbortController().signal;
		const first = await detector.open({ format: inputFormat(), signal });
		const firstFrame = new Int16Array(512);
		firstFrame[0] = -32_768;
		firstFrame[1] = 16_384;
		firstFrame[448] = 8192;

		await expect(first.detect({ chunk: { bytes: pcmBytes(firstFrame) }, signal })).resolves.toBe(0.75);
		await expect(first.detect({ chunk: { bytes: pcmBytes(new Int16Array(512)) }, signal })).resolves.toBe(0.25);
		first.close();
		const second = await detector.open({ format: inputFormat(), signal });
		await second.detect({ chunk: { bytes: pcmBytes(new Int16Array(512)) }, signal });

		expect(runs).toHaveLength(3);
		const [firstRun, secondRun, thirdRun] = runs;
		if (!firstRun || !secondRun || !thirdRun) throw new Error("expected three inference runs");
		expect(firstRun.input.dims).toEqual([1, 576]);
		expect(Array.from((firstRun.input.data as Float32Array).slice(0, 66))).toEqual([...new Array(64).fill(0), -1, 0.5]);
		expect((firstRun.state.data as Float32Array).every((value) => value === 0)).toBe(true);
		expect(firstRun.sr.dims).toEqual([]);
		expect(Array.from(firstRun.sr.data as BigInt64Array)).toEqual([16_000n]);
		expect(Array.from((secondRun.input.data as Float32Array).slice(0, 64))).toEqual([0.25, ...new Array(63).fill(0)]);
		expect((secondRun.state.data as Float32Array).every((value) => value === 3)).toBe(true);
		expect((thirdRun.input.data as Float32Array).every((value) => value === 0)).toBe(true);
		expect((thirdRun.state.data as Float32Array).every((value) => value === 0)).toBe(true);
		second.close();
		await detector.dispose();
	});

	it("times out non-cooperative inference without committing late state or leaking late tensors", async () => {
		vi.useFakeTimers();
		const artifact = await modelArtifact(Uint8Array.of(7, 8));
		let finishLate: ((outputs: Record<string, InstanceType<typeof ort.Tensor>>) => void) | undefined;
		const late = new Promise<Record<string, InstanceType<typeof ort.Tensor>>>((resolve) => {
			finishLate = resolve;
		});
		const runs: Array<{
			readonly feeds: FakeFeeds;
			readonly options: { terminate?: boolean };
		}> = [];
		let call = 0;
		ort.create.mockResolvedValue(
			modelSession(async (feeds, runOptions) => {
				runs.push({ feeds, options: runOptions });
				call += 1;
				if (call === 1) return await late;
				return {
					output: new ort.Tensor("float32", Float32Array.of(0.4), [1, 1]),
					stateN: new ort.Tensor("float32", new Float32Array(256), [2, 1, 128]),
				};
			}),
		);
		const detector = new SileroVoiceActivityDetector(options(artifact, { inferenceTimeoutMs: 10 }));
		const signal = new AbortController().signal;
		const session = await detector.open({ format: inputFormat(), signal });
		const timedOut = session.detect({ chunk: { bytes: pcmBytes(new Int16Array(512).fill(1234)) }, signal });
		const timeoutAssertion = expect(timedOut).rejects.toEqual(new SileroVoiceActivityError("inference_timeout"));

		await vi.advanceTimersByTimeAsync(10);
		await timeoutAssertion;
		expect(runs[0]?.options.terminate).toBe(true);
		await expect(session.detect({ chunk: { bytes: pcmBytes(new Int16Array(512)) }, signal })).rejects.toEqual(
			new SileroVoiceActivityError("session_busy"),
		);
		const lateOutput = new ort.Tensor("float32", Float32Array.of(0.9), [1, 1]);
		const lateState = new ort.Tensor("float32", new Float32Array(256).fill(9), [2, 1, 128]);
		finishLate?.({ output: lateOutput, stateN: lateState });
		await Promise.resolve();
		await Promise.resolve();
		await expect(session.detect({ chunk: { bytes: pcmBytes(new Int16Array(512)) }, signal })).resolves.toBeCloseTo(0.4);

		expect(lateOutput.disposed).toBe(1);
		expect(lateState.disposed).toBe(1);
		const recoveredRun = runs[1];
		if (!recoveredRun) throw new Error("expected recovery inference");
		expect((recoveredRun.feeds.input.data as Float32Array).every((value) => value === 0)).toBe(true);
		expect((recoveredRun.feeds.state.data as Float32Array).every((value) => value === 0)).toBe(true);
		session.close();
		await detector.dispose();
	});

	it("preserves caller cancellation, disposes late inference outputs, and keeps the stream reusable", async () => {
		const artifact = await modelArtifact(Uint8Array.of(20));
		let finishLate: ((outputs: Record<string, InstanceType<typeof ort.Tensor>>) => void) | undefined;
		const late = new Promise<Record<string, InstanceType<typeof ort.Tensor>>>((resolve) => {
			finishLate = resolve;
		});
		const runs: Array<{ readonly feeds: FakeFeeds; readonly options: { terminate?: boolean } }> = [];
		ort.create.mockResolvedValue(
			modelSession(async (feeds, runOptions) => {
				runs.push({ feeds, options: runOptions });
				if (runs.length === 1) return await late;
				return {
					output: new ort.Tensor("float32", Float32Array.of(0.2), [1, 1]),
					stateN: new ort.Tensor("float32", new Float32Array(256), [2, 1, 128]),
				};
			}),
		);
		const detector = new SileroVoiceActivityDetector(options(artifact));
		const sessionSignal = new AbortController().signal;
		const session = await detector.open({ format: inputFormat(), signal: sessionSignal });
		const request = new AbortController();
		const reason = new Error("microphone frame revoked");
		const pending = session.detect({
			chunk: { bytes: pcmBytes(new Int16Array(512).fill(3210)) },
			signal: request.signal,
		});
		await vi.waitFor(() => expect(runs).toHaveLength(1));

		request.abort(reason);
		await expect(pending).rejects.toBe(reason);
		expect(runs[0]?.options.terminate).toBe(true);
		const lateOutput = new ort.Tensor("float32", Float32Array.of(0.9), [1, 1]);
		const lateState = new ort.Tensor("float32", new Float32Array(256).fill(9), [2, 1, 128]);
		finishLate?.({ output: lateOutput, stateN: lateState });
		await vi.waitFor(() => expect(lateOutput.disposed).toBe(1));
		await expect(
			session.detect({ chunk: { bytes: pcmBytes(new Int16Array(512)) }, signal: sessionSignal }),
		).resolves.toBeCloseTo(0.2);
		const recovered = runs[1];
		if (!recovered) throw new Error("expected recovery inference");
		expect((recovered.feeds.input.data as Float32Array).every((value) => value === 0)).toBe(true);
		expect((recovered.feeds.state.data as Float32Array).every((value) => value === 0)).toBe(true);
		expect(lateState.disposed).toBe(1);
		session.close();
		await detector.dispose();
	});

	it("distinguishes one busy stream from detector-wide stream capacity", async () => {
		const artifact = await modelArtifact(Uint8Array.of(16));
		let finishInference: ((outputs: Record<string, InstanceType<typeof ort.Tensor>>) => void) | undefined;
		ort.create.mockResolvedValue(
			modelSession(
				() =>
					new Promise((resolve) => {
						finishInference = resolve;
					}),
			),
		);
		const detector = new SileroVoiceActivityDetector(options(artifact));
		const signal = new AbortController().signal;
		const session = await detector.open({ format: inputFormat(), signal });
		const first = session.detect({ chunk: { bytes: pcmBytes(new Int16Array(512)) }, signal });

		await expect(session.detect({ chunk: { bytes: pcmBytes(new Int16Array(512)) }, signal })).rejects.toEqual(
			new SileroVoiceActivityError("session_busy"),
		);
		finishInference?.({
			output: new ort.Tensor("float32", Float32Array.of(0.5), [1, 1]),
			stateN: new ort.Tensor("float32", new Float32Array(256), [2, 1, 128]),
		});
		await expect(first).resolves.toBe(0.5);
		session.close();
		await detector.dispose();
	});

	it("redacts tensor construction failures and releases tensors already created for that inference", async () => {
		const artifact = await modelArtifact(Uint8Array.of(17));
		const model = modelSession(async () => ({
			output: new ort.Tensor("float32", Float32Array.of(0), [1, 1]),
			stateN: new ort.Tensor("float32", new Float32Array(256), [2, 1, 128]),
		}));
		ort.create.mockResolvedValue(model);
		const detector = new SileroVoiceActivityDetector(options(artifact));
		const signal = new AbortController().signal;
		const session = await detector.open({ format: inputFormat(), signal });
		ort.tensorState.failAtCreation = 2;

		await expect(session.detect({ chunk: { bytes: pcmBytes(new Int16Array(512)) }, signal })).rejects.toEqual(
			new SileroVoiceActivityError("inference_failed"),
		);
		expect(ort.tensorState.created[0]?.disposed).toBe(1);
		expect(model.run).not.toHaveBeenCalled();
		session.close();
		await detector.dispose();
	});

	it("redacts malformed model outputs, disposes captured tensors, and leaves recurrent state untouched", async () => {
		const artifact = await modelArtifact(Uint8Array.of(10, 11));
		const firstOutput = new ort.Tensor("float32", Float32Array.of(0.8), [1, 1]);
		const runs: FakeFeeds[] = [];
		let call = 0;
		ort.create.mockResolvedValue(
			modelSession(async (feeds) => {
				runs.push(feeds);
				call += 1;
				if (call === 1) {
					return Object.defineProperties(
						{},
						{
							output: {
								get: () => firstOutput,
							},
							stateN: {
								get: () => {
									throw new Error("sensitive backend detail");
								},
							},
						},
					) as Record<string, InstanceType<typeof ort.Tensor>>;
				}
				return {
					output: new ort.Tensor("float32", Float32Array.of(0.6), [1, 1]),
					stateN: new ort.Tensor("float32", new Float32Array(256), [2, 1, 128]),
				};
			}),
		);
		const detector = new SileroVoiceActivityDetector(options(artifact));
		const signal = new AbortController().signal;
		const session = await detector.open({ format: inputFormat(), signal });

		await expect(
			session.detect({ chunk: { bytes: pcmBytes(new Int16Array(512).fill(2222)) }, signal }),
		).rejects.toEqual(new SileroVoiceActivityError("inference_failed"));
		await expect(session.detect({ chunk: { bytes: pcmBytes(new Int16Array(512)) }, signal })).resolves.toBeCloseTo(0.6);

		expect(firstOutput.disposed).toBe(1);
		const recoveredRun = runs[1];
		if (!recoveredRun) throw new Error("expected recovery inference");
		expect((recoveredRun.input.data as Float32Array).every((value) => value === 0)).toBe(true);
		expect((recoveredRun.state.data as Float32Array).every((value) => value === 0)).toBe(true);
		session.close();
		await detector.dispose();
	});

	it("redacts non-finite and out-of-range speech probabilities without committing recurrent state", async () => {
		const artifact = await modelArtifact(Uint8Array.of(18));
		const runs: FakeFeeds[] = [];
		const probabilities = [Number.NaN, 1.01, 0.25];
		ort.create.mockResolvedValue(
			modelSession(async (feeds) => {
				runs.push(feeds);
				return {
					output: new ort.Tensor("float32", Float32Array.of(probabilities.shift() ?? 0), [1, 1]),
					stateN: new ort.Tensor("float32", new Float32Array(256).fill(7), [2, 1, 128]),
				};
			}),
		);
		const detector = new SileroVoiceActivityDetector(options(artifact));
		const signal = new AbortController().signal;
		const session = await detector.open({ format: inputFormat(), signal });
		const voicedFrame = pcmBytes(new Int16Array(512).fill(1234));

		for (let attempt = 0; attempt < 2; attempt += 1) {
			await expect(session.detect({ chunk: { bytes: voicedFrame }, signal })).rejects.toEqual(
				new SileroVoiceActivityError("inference_failed"),
			);
		}
		await expect(session.detect({ chunk: { bytes: pcmBytes(new Int16Array(512)) }, signal })).resolves.toBe(0.25);

		const recoveredRun = runs[2];
		if (!recoveredRun) throw new Error("expected recovery inference");
		expect((recoveredRun.input.data as Float32Array).every((value) => value === 0)).toBe(true);
		expect((recoveredRun.state.data as Float32Array).every((value) => value === 0)).toBe(true);
		session.close();
		await detector.dispose();
	});

	it("rejects malformed model metadata with one stable error and releases the model", async () => {
		const artifact = await modelArtifact(Uint8Array.of(19));
		const model = modelSession(async () => ({
			output: new ort.Tensor("float32", Float32Array.of(0), [1, 1]),
			stateN: new ort.Tensor("float32", new Float32Array(256), [2, 1, 128]),
		}));
		model.inputMetadata[0] = {
			name: "input",
			isTensor: true,
			type: "sensitive-invalid-type",
			shape: [4_294_967_295, 4_294_967_295],
		};
		ort.create.mockResolvedValue(model);
		const detector = new SileroVoiceActivityDetector(options(artifact));

		await expect(detector.open({ format: inputFormat(), signal: new AbortController().signal })).rejects.toEqual(
			new SileroVoiceActivityError("invalid_model"),
		);
		await vi.waitFor(() => expect(model.release).toHaveBeenCalledTimes(1));
		await detector.dispose();
	});

	it("cancels a pending model open on disposal and releases the late ONNX session once", async () => {
		const artifact = await modelArtifact(Uint8Array.of(12, 13));
		let finishOpen: ((session: ReturnType<typeof modelSession>) => void) | undefined;
		const pending = new Promise<ReturnType<typeof modelSession>>((resolve) => {
			finishOpen = resolve;
		});
		ort.create.mockReturnValue(pending);
		const detector = new SileroVoiceActivityDetector(options(artifact, { modelLoadTimeoutMs: 1000 }));
		const opening = detector.open({ format: inputFormat(), signal: new AbortController().signal });
		const openingOutcome = opening.then(
			() => undefined,
			(error: unknown) => error,
		);
		await vi.waitFor(() => expect(ort.create).toHaveBeenCalledTimes(1));

		await detector.dispose();
		await expect(openingOutcome).resolves.toEqual(new SileroVoiceActivityError("detector_disposed"));
		const late = modelSession(async () => ({
			output: new ort.Tensor("float32", Float32Array.of(0), [1, 1]),
			stateN: new ort.Tensor("float32", new Float32Array(256), [2, 1, 128]),
		}));
		finishOpen?.(late);
		await Promise.resolve();
		await Promise.resolve();

		expect(late.release).toHaveBeenCalledTimes(1);
	});

	it("waits for cancelled inference to drain before releasing its shared model", async () => {
		const artifact = await modelArtifact(Uint8Array.of(21));
		let finishInference: ((outputs: Record<string, InstanceType<typeof ort.Tensor>>) => void) | undefined;
		const pendingInference = new Promise<Record<string, InstanceType<typeof ort.Tensor>>>((resolve) => {
			finishInference = resolve;
		});
		const model = modelSession(async () => await pendingInference);
		ort.create.mockResolvedValue(model);
		const detector = new SileroVoiceActivityDetector(options(artifact));
		const signal = new AbortController().signal;
		const session = await detector.open({ format: inputFormat(), signal });
		const detection = session.detect({ chunk: { bytes: pcmBytes(new Int16Array(512)) }, signal });
		await vi.waitFor(() => expect(model.run).toHaveBeenCalledTimes(1));

		let disposed = false;
		const disposing = detector.dispose().then(() => {
			disposed = true;
		});
		await expect(detection).rejects.toEqual(new SileroVoiceActivityError("detector_disposed"));
		await Promise.resolve();
		expect({ disposed, releases: model.release.mock.calls.length }).toEqual({ disposed: false, releases: 0 });
		const output = new ort.Tensor("float32", Float32Array.of(0.5), [1, 1]);
		const state = new ort.Tensor("float32", new Float32Array(256), [2, 1, 128]);
		finishInference?.({ output, stateN: state });
		await disposing;

		expect({ disposed, outputDisposals: output.disposed, stateDisposals: state.disposed }).toEqual({
			disposed: true,
			outputDisposals: 1,
			stateDisposals: 1,
		});
		expect(model.release).toHaveBeenCalledTimes(1);
	});

	it("accepts only exact mono PCM16 16 kHz sessions and 1024-byte frames", async () => {
		const artifact = await modelArtifact(Uint8Array.of(14));
		const model = modelSession(async () => ({
			output: new ort.Tensor("float32", Float32Array.of(0), [1, 1]),
			stateN: new ort.Tensor("float32", new Float32Array(256), [2, 1, 128]),
		}));
		ort.create.mockResolvedValue(model);
		const detector = new SileroVoiceActivityDetector(options(artifact));
		const signal = new AbortController().signal;

		await expect(detector.open({ format: { ...inputFormat(), sampleRateHz: 8000 }, signal })).rejects.toBeInstanceOf(
			LocalSpeechProtocolError,
		);
		expect(ort.create).not.toHaveBeenCalled();
		const session = await detector.open({ format: inputFormat(), signal });
		await expect(session.detect({ chunk: { bytes: new Uint8Array(1023) }, signal })).rejects.toBeInstanceOf(
			LocalSpeechProtocolError,
		);
		expect(model.run).not.toHaveBeenCalled();
		session.close();
		await detector.dispose();
	});

	it("bounds concurrent streams and releases one shared model exactly once", async () => {
		const artifact = await modelArtifact(Uint8Array.of(15));
		const model = modelSession(async () => ({
			output: new ort.Tensor("float32", Float32Array.of(0), [1, 1]),
			stateN: new ort.Tensor("float32", new Float32Array(256), [2, 1, 128]),
		}));
		ort.create.mockResolvedValue(model);
		const detector = new SileroVoiceActivityDetector(options(artifact, { maxConcurrentSessions: 1 }));
		const request = { format: inputFormat(), signal: new AbortController().signal };
		const first = await detector.open(request);

		await expect(detector.open(request)).rejects.toEqual(new SileroVoiceActivityError("session_capacity"));
		first.close();
		const second = await detector.open(request);
		second.close();
		await detector.dispose();
		await detector.dispose();

		expect(model.release).toHaveBeenCalledTimes(1);
		await expect(detector.open(request)).rejects.toEqual(new SileroVoiceActivityError("detector_disposed"));
	});
});

function inputFormat() {
	return { mediaType: "audio/pcm;format=s16le" as const, sampleRateHz: 16_000, channelCount: 1 };
}

function options(
	model: { readonly path: string; readonly sha256: string },
	overrides: Partial<{
		readonly maxModelPathBytes: number;
		readonly maxModelBytes: number;
		readonly modelLoadTimeoutMs: number;
		readonly inferenceTimeoutMs: number;
		readonly maxConcurrentSessions: number;
	}> = {},
) {
	return {
		model,
		maxModelPathBytes: 1024,
		maxModelBytes: 1024,
		modelLoadTimeoutMs: 1000,
		inferenceTimeoutMs: 1000,
		maxConcurrentSessions: 2,
		...overrides,
	};
}

function pcmBytes(samples: Int16Array): Uint8Array {
	return new Uint8Array(samples.buffer.slice(samples.byteOffset, samples.byteOffset + samples.byteLength));
}

function modelSession(
	run: (feeds: FakeFeeds, options: { terminate?: boolean }) => Promise<Record<string, FakeTensor>>,
) {
	return {
		inputNames: ["input", "state", "sr"],
		outputNames: ["output", "stateN"],
		inputMetadata: [
			{ name: "input", isTensor: true, type: "float32", shape: [4_294_967_295, 4_294_967_295] },
			{ name: "state", isTensor: true, type: "float32", shape: [2, 4_294_967_295, 128] },
			{ name: "sr", isTensor: true, type: "int64", shape: [] },
		],
		outputMetadata: [
			{ name: "output", isTensor: true, type: "float32", shape: [4_294_967_295, 1] },
			{ name: "stateN", isTensor: true, type: "float32", shape: [4_294_967_295, 4_294_967_295, 4_294_967_295] },
		],
		run: vi.fn(run),
		release: vi.fn(async () => undefined),
	};
}

async function modelArtifact(bytes: Uint8Array): Promise<{ readonly path: string; readonly sha256: string }> {
	const directory = await mkdtemp(join(tmpdir(), "oga-silero-test-"));
	directories.push(directory);
	const path = join(directory, "silero_vad.onnx");
	await writeFile(path, bytes);
	return { path, sha256: createHash("sha256").update(bytes).digest("hex") };
}
