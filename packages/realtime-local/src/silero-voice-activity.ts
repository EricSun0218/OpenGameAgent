import { Buffer, constants as bufferConstants } from "node:buffer";
import { createHash } from "node:crypto";
import { type FileHandle, open as openFile } from "node:fs/promises";
import { raceOperationWithAbort } from "@opengameagent/protocol";
import { InferenceSession, Tensor } from "onnxruntime-web";
import { LocalSpeechProtocolError } from "./errors.js";
import { detachCleanup } from "./iterator.js";
import type {
	RealtimeVoiceActivityDetector,
	RealtimeVoiceActivitySession,
	VoiceActivityDetectionRequest,
	VoiceActivitySessionRequest,
} from "./types.js";
import { PCM_S16LE_MEDIA_TYPE } from "./types.js";
import { assertPositiveSafeInteger, assertTimer, inspectBytesWithAbort } from "./validation.js";

const SHA256 = /^[a-f\d]{64}$/;
const SAMPLE_RATE_HZ = 16_000;
const FRAME_SAMPLES = 512;
const FRAME_BYTES = FRAME_SAMPLES * 2;
const CONTEXT_SAMPLES = 64;
const INPUT_SAMPLES = CONTEXT_SAMPLES + FRAME_SAMPLES;
const STATE_SAMPLES = 2 * 1 * 128;
const RELEASED_MODELS = new WeakSet<object>();

export interface SileroModelArtifact {
	readonly path: string;
	readonly sha256: string;
}

export interface SileroVoiceActivityDetectorOptions {
	readonly model: Readonly<SileroModelArtifact>;
	readonly maxModelPathBytes: number;
	readonly maxModelBytes: number;
	readonly modelLoadTimeoutMs: number;
	readonly inferenceTimeoutMs: number;
	readonly maxConcurrentSessions: number;
}

export type SileroVoiceActivityErrorCode =
	| "detector_disposed"
	| "inference_failed"
	| "inference_timeout"
	| "invalid_artifact"
	| "invalid_configuration"
	| "invalid_model"
	| "model_load_timeout"
	| "session_capacity"
	| "session_busy"
	| "session_closed";

export class SileroVoiceActivityError extends Error {
	override readonly name = "SileroVoiceActivityError";

	constructor(readonly code: SileroVoiceActivityErrorCode) {
		super(`silero_voice_activity_${code}`);
	}
}

interface SileroConfiguration {
	readonly model: Readonly<SileroModelArtifact>;
	readonly maxModelBytes: number;
	readonly modelLoadTimeoutMs: number;
	readonly inferenceTimeoutMs: number;
	readonly maxConcurrentSessions: number;
}

interface CapturedModelSession {
	readonly raw: object;
	readonly run: InferenceSession["run"];
	readonly release: () => Promise<void>;
}

export class SileroVoiceActivityDetector implements RealtimeVoiceActivityDetector, AsyncDisposable {
	private readonly configuration: SileroConfiguration;
	private readonly lifetime = new AbortController();
	private readonly sessions = new Set<SileroVoiceActivitySession>();
	private modelPromise: Promise<CapturedModelSession> | undefined;
	private model: CapturedModelSession | undefined;
	private disposed = false;

	constructor(options: Readonly<SileroVoiceActivityDetectorOptions>) {
		this.configuration = validateOptions(options);
	}

	async open(request: Readonly<VoiceActivitySessionRequest>): Promise<RealtimeVoiceActivitySession> {
		const callerSignal = captureOpenRequest(request);
		if (this.disposed) throw new SileroVoiceActivityError("detector_disposed");
		const signal = AbortSignal.any([callerSignal, this.lifetime.signal]);
		const model = await this.ensureModel(signal);
		signal.throwIfAborted();
		if (this.disposed) throw new SileroVoiceActivityError("detector_disposed");
		if (this.sessions.size >= this.configuration.maxConcurrentSessions) {
			throw new SileroVoiceActivityError("session_capacity");
		}
		const lifetime = new AbortController();
		let session: SileroVoiceActivitySession;
		session = new SileroVoiceActivitySession(model, lifetime, this.configuration.inferenceTimeoutMs, () =>
			this.sessions.delete(session),
		);
		this.sessions.add(session);
		return session;
	}

	async dispose(): Promise<void> {
		if (this.disposed) return;
		this.disposed = true;
		const reason = new SileroVoiceActivityError("detector_disposed");
		this.lifetime.abort(reason);
		await Promise.all([...this.sessions].map((session) => session.dispose(reason)));
		if (this.model) await this.model.release();
		else if (this.modelPromise)
			void this.modelPromise.then(
				(model) => model.release(),
				() => undefined,
			);
	}

	async [Symbol.asyncDispose](): Promise<void> {
		await this.dispose();
	}

	private async ensureModel(signal: AbortSignal): Promise<CapturedModelSession> {
		if (!this.modelPromise) {
			const loading = this.loadModel();
			this.modelPromise = loading;
			void loading.then(
				(model) => {
					this.model = model;
					if (this.disposed) void model.release();
				},
				() => undefined,
			);
		}
		return await raceOperationWithAbort(() => this.modelPromise as Promise<CapturedModelSession>, signal);
	}

	private async loadModel(): Promise<CapturedModelSession> {
		const timeout = deadline(this.configuration.modelLoadTimeoutMs, "model_load_timeout");
		const signal = AbortSignal.any([this.lifetime.signal, timeout.signal]);
		let creating: Promise<InferenceSession> | undefined;
		try {
			const bytes = await raceOperationWithAbort(
				() => readVerifiedModel(this.configuration.model, this.configuration.maxModelBytes, signal),
				signal,
			);
			signal.throwIfAborted();
			try {
				creating = Promise.resolve(InferenceSession.create(bytes, { executionProviders: ["wasm"] }));
			} catch {
				throw new SileroVoiceActivityError("invalid_model");
			}
			const raw = await raceOperationWithAbort(() => creating as Promise<InferenceSession>, signal);
			const model = captureModelSession(raw);
			signal.throwIfAborted();
			return model;
		} catch (error) {
			if (creating) closeLateModel(creating);
			if (signal.aborted) throw signal.reason;
			if (error instanceof SileroVoiceActivityError) throw error;
			throw new SileroVoiceActivityError("invalid_model");
		} finally {
			timeout.clear();
		}
	}
}

class SileroVoiceActivitySession implements RealtimeVoiceActivitySession {
	private state = new Float32Array(STATE_SAMPLES);
	private context = new Float32Array(CONTEXT_SAMPLES);
	private closed = false;
	private busy = false;
	private closeNotified = false;
	private readonly closedPromise: Promise<void>;
	private resolveClosed!: () => void;

	constructor(
		private readonly model: CapturedModelSession,
		private readonly lifetime: AbortController,
		private readonly inferenceTimeoutMs: number,
		private readonly onClose: () => void,
	) {
		this.closedPromise = new Promise((resolve) => {
			this.resolveClosed = resolve;
		});
	}

	async detect(request: Readonly<VoiceActivityDetectionRequest>): Promise<number> {
		if (this.closed) throw new SileroVoiceActivityError("session_closed");
		if (this.busy) throw new SileroVoiceActivityError("session_busy");
		const captured = captureDetectionRequest(request);
		const signal = AbortSignal.any([captured.signal, this.lifetime.signal]);
		signal.throwIfAborted();
		const samples = decodeFrame(captured.chunk, signal);
		const input = new Float32Array(INPUT_SAMPLES);
		input.set(this.context);
		input.set(samples, CONTEXT_SAMPLES);
		let inputTensor: Tensor | undefined;
		let stateTensor: Tensor | undefined;
		let sampleRateTensor: Tensor | undefined;
		let timeout: ReturnType<typeof deadline> | undefined;
		let operationSignal = signal;
		const runOptions: InferenceSession.RunOptions = { terminate: false };
		const terminate = () => {
			runOptions.terminate = true;
		};
		this.busy = true;
		let outputs: InferenceSession.ReturnType | undefined;
		let capturedOutputs: { readonly output: unknown; readonly stateN: unknown } | undefined;
		let running: Promise<InferenceSession.ReturnType> | undefined;
		let cleanupDeferred = false;
		let cleaned = false;
		const cleanup = (lateOutputs?: unknown) => {
			if (cleaned) return;
			cleaned = true;
			disposeTensor(inputTensor);
			disposeTensor(stateTensor);
			disposeTensor(sampleRateTensor);
			if (capturedOutputs) {
				disposeTensor(capturedOutputs.output);
				disposeTensor(capturedOutputs.stateN);
			} else if (lateOutputs) disposeInferenceOutputs(lateOutputs);
			else if (outputs) disposeInferenceOutputs(outputs);
			this.finishInference();
		};
		try {
			inputTensor = new Tensor("float32", input, [1, INPUT_SAMPLES]);
			stateTensor = new Tensor("float32", this.state.slice(), [2, 1, 128]);
			sampleRateTensor = new Tensor("int64", BigInt64Array.of(BigInt(SAMPLE_RATE_HZ)), []);
			const feeds = { input: inputTensor, state: stateTensor, sr: sampleRateTensor };
			timeout = deadline(this.inferenceTimeoutMs, "inference_timeout");
			operationSignal = AbortSignal.any([signal, timeout.signal]);
			operationSignal.addEventListener("abort", terminate, { once: true });
			running = Promise.resolve(Reflect.apply(this.model.run, this.model.raw, [feeds, runOptions]));
			outputs = await raceOperationWithAbort(() => running as Promise<InferenceSession.ReturnType>, operationSignal);
			operationSignal.throwIfAborted();
			try {
				capturedOutputs = captureInferenceOutputs(outputs);
			} catch (error) {
				outputs = undefined;
				throw error;
			}
			const probability = captureFloat32Tensor(capturedOutputs.output, [1, 1]);
			const nextState = captureFloat32Tensor(capturedOutputs.stateN, [2, 1, 128]);
			const value = probability.data[0];
			if (value === undefined || !Number.isFinite(value) || value < 0 || value > 1) {
				throw new SileroVoiceActivityError("inference_failed");
			}
			operationSignal.throwIfAborted();
			this.state = nextState.data.slice();
			this.context = samples.slice(FRAME_SAMPLES - CONTEXT_SAMPLES);
			return value;
		} catch (error) {
			if (operationSignal.aborted) {
				if (running && outputs === undefined) {
					cleanupDeferred = true;
					void running.then(cleanup, () => cleanup());
				}
				throw operationSignal.reason;
			}
			if (error instanceof SileroVoiceActivityError) throw error;
			throw new SileroVoiceActivityError("inference_failed");
		} finally {
			operationSignal.removeEventListener("abort", terminate);
			timeout?.clear();
			if (!cleanupDeferred) cleanup();
		}
	}

	close(): Promise<void> {
		return this.dispose(new SileroVoiceActivityError("session_closed"));
	}

	dispose(reason: unknown): Promise<void> {
		if (!this.closed) {
			this.closed = true;
			this.lifetime.abort(reason);
			if (!this.busy) this.notifyClose();
		}
		return this.closedPromise;
	}

	private finishInference(): void {
		this.busy = false;
		if (this.closed) this.notifyClose();
	}

	private notifyClose(): void {
		if (this.closeNotified) return;
		this.closeNotified = true;
		this.onClose();
		this.resolveClosed();
	}
}

function validateOptions(options: Readonly<SileroVoiceActivityDetectorOptions>): SileroConfiguration {
	try {
		if (!options || typeof options !== "object") throw new Error();
		const model = options.model;
		const path = model?.path;
		const sha256 = model?.sha256;
		const maxModelPathBytes = options.maxModelPathBytes;
		const maxModelBytes = options.maxModelBytes;
		const modelLoadTimeoutMs = options.modelLoadTimeoutMs;
		const inferenceTimeoutMs = options.inferenceTimeoutMs;
		const maxConcurrentSessions = options.maxConcurrentSessions;
		assertPositiveSafeInteger(maxModelPathBytes);
		assertPositiveSafeInteger(maxModelBytes);
		assertTimer(modelLoadTimeoutMs);
		assertTimer(inferenceTimeoutMs);
		assertPositiveSafeInteger(maxConcurrentSessions);
		if (
			typeof path !== "string" ||
			path.length === 0 ||
			Buffer.byteLength(path, "utf8") > maxModelPathBytes ||
			typeof sha256 !== "string" ||
			!SHA256.test(sha256) ||
			maxModelBytes > bufferConstants.MAX_LENGTH
		) {
			throw new Error();
		}
		return Object.freeze({
			model: Object.freeze({ path, sha256 }),
			maxModelBytes,
			modelLoadTimeoutMs,
			inferenceTimeoutMs,
			maxConcurrentSessions,
		});
	} catch {
		throw new SileroVoiceActivityError("invalid_configuration");
	}
}

function captureOpenRequest(request: Readonly<VoiceActivitySessionRequest>): AbortSignal {
	let signal: AbortSignal | undefined;
	try {
		if (!request || typeof request !== "object") throw new Error();
		signal = request.signal;
		const format = request.format;
		if (
			!(signal instanceof AbortSignal) ||
			format?.mediaType !== PCM_S16LE_MEDIA_TYPE ||
			format.sampleRateHz !== SAMPLE_RATE_HZ ||
			format.channelCount !== 1
		) {
			throw new Error();
		}
		signal.throwIfAborted();
		return signal;
	} catch {
		if (signal?.aborted) throw signal.reason;
		throw new LocalSpeechProtocolError();
	}
}

function captureDetectionRequest(request: Readonly<VoiceActivityDetectionRequest>): {
	readonly signal: AbortSignal;
	readonly chunk: Readonly<VoiceActivityDetectionRequest["chunk"]>;
} {
	let signal: AbortSignal | undefined;
	try {
		if (!request || typeof request !== "object") throw new Error();
		signal = request.signal;
		const chunk = request.chunk;
		if (!(signal instanceof AbortSignal)) throw new Error();
		signal.throwIfAborted();
		return { signal, chunk };
	} catch {
		if (signal?.aborted) throw signal.reason;
		throw new LocalSpeechProtocolError();
	}
}

function decodeFrame(chunk: Readonly<VoiceActivityDetectionRequest["chunk"]>, signal: AbortSignal): Float32Array {
	const view = inspectBytesWithAbort(chunk, signal);
	if (view.byteLength !== FRAME_BYTES) throw new LocalSpeechProtocolError();
	const data = new DataView(view.buffer, view.byteOffset, view.byteLength);
	const samples = new Float32Array(FRAME_SAMPLES);
	for (let index = 0; index < FRAME_SAMPLES; index += 1) samples[index] = data.getInt16(index * 2, true) / 32_768;
	signal.throwIfAborted();
	return samples;
}

async function readVerifiedModel(
	artifact: Readonly<SileroModelArtifact>,
	maximum: number,
	signal: AbortSignal,
): Promise<Uint8Array> {
	let handle: FileHandle | undefined;
	try {
		handle = await openFile(artifact.path, "r");
		signal.throwIfAborted();
		const info = await handle.stat();
		signal.throwIfAborted();
		if (!info.isFile() || info.size < 1 || info.size > maximum || !Number.isSafeInteger(info.size)) {
			throw new SileroVoiceActivityError("invalid_artifact");
		}
		const bytes = new Uint8Array(info.size);
		let offset = 0;
		while (offset < bytes.byteLength) {
			const result = await handle.read(bytes, offset, bytes.byteLength - offset, offset);
			signal.throwIfAborted();
			if (result.bytesRead === 0) throw new SileroVoiceActivityError("invalid_artifact");
			offset += result.bytesRead;
		}
		const extra = new Uint8Array(1);
		if ((await handle.read(extra, 0, 1, offset)).bytesRead !== 0) {
			throw new SileroVoiceActivityError("invalid_artifact");
		}
		signal.throwIfAborted();
		if (createHash("sha256").update(bytes).digest("hex") !== artifact.sha256) {
			throw new SileroVoiceActivityError("invalid_artifact");
		}
		return bytes;
	} catch (error) {
		signal.throwIfAborted();
		if (error instanceof SileroVoiceActivityError) throw error;
		throw new SileroVoiceActivityError("invalid_artifact");
	} finally {
		await handle?.close().catch(() => undefined);
	}
}

function captureModelSession(value: unknown): CapturedModelSession {
	if (!value || typeof value !== "object") throw new SileroVoiceActivityError("invalid_model");
	let release: unknown;
	try {
		const session = value as InferenceSession;
		const run = session.run;
		release = session.release;
		if (typeof run !== "function" || typeof release !== "function") throw new Error();
		validateMetadata(session.inputNames, session.inputMetadata, [
			["input", "float32", 2, []],
			["state", "float32", 3, [2, undefined, 128]],
			["sr", "int64", 0, []],
		]);
		validateMetadata(session.outputNames, session.outputMetadata, [
			["output", "float32", 2, [undefined, 1]],
			["stateN", "float32", 3, []],
		]);
		return Object.freeze({
			raw: value,
			run,
			release: () => releaseRawModel(value, release as InferenceSession["release"]),
		});
	} catch {
		if (typeof release === "function")
			detachCleanup(() => releaseRawModel(value, release as InferenceSession["release"]));
		throw new SileroVoiceActivityError("invalid_model");
	}
}

type ExpectedMetadata = readonly [
	name: string,
	type: "float32" | "int64",
	rank: number,
	fixedDimensions: readonly (number | undefined)[],
];

function validateMetadata(
	names: readonly string[],
	metadata: readonly InferenceSession.ValueMetadata[],
	expected: readonly ExpectedMetadata[],
): void {
	if (
		!Array.isArray(names) ||
		!Array.isArray(metadata) ||
		names.length !== expected.length ||
		metadata.length !== expected.length
	) {
		throw new Error();
	}
	for (const [name, type, rank, fixed] of expected) {
		if (!names.includes(name)) throw new Error();
		const entry = metadata.find((candidate) => candidate.name === name);
		if (!entry?.isTensor || entry.type !== type || !Array.isArray(entry.shape) || entry.shape.length !== rank) {
			throw new Error();
		}
		for (let index = 0; index < fixed.length; index += 1) {
			const dimension = fixed[index];
			if (dimension !== undefined && entry.shape[index] !== dimension) throw new Error();
		}
	}
}

function captureFloat32Tensor(value: unknown, expectedDimensions: readonly number[]) {
	try {
		if (!value || typeof value !== "object") throw new Error();
		const tensor = value as Tensor;
		const type = tensor.type;
		const dimensions = tensor.dims;
		const data = tensor.data;
		if (
			type !== "float32" ||
			!Array.isArray(dimensions) ||
			dimensions.length !== expectedDimensions.length ||
			dimensions.some((dimension, index) => dimension !== expectedDimensions[index]) ||
			!(data instanceof Float32Array) ||
			data.length !== expectedDimensions.reduce((total, dimension) => total * dimension, 1)
		) {
			throw new Error();
		}
		return { data };
	} catch {
		throw new SileroVoiceActivityError("inference_failed");
	}
}

function disposeTensor(value: unknown): void {
	if (!value || typeof value !== "object") return;
	try {
		const dispose = (value as Tensor).dispose;
		if (typeof dispose === "function") dispose.call(value);
	} catch {
		// Tensor cleanup cannot replace the inference result.
	}
}

function captureInferenceOutputs(value: unknown): { readonly output: unknown; readonly stateN: unknown } {
	if (!value || typeof value !== "object") throw new SileroVoiceActivityError("inference_failed");
	let output: unknown;
	try {
		output = (value as InferenceSession.ReturnType).output;
		const stateN = (value as InferenceSession.ReturnType).stateN;
		return { output, stateN };
	} catch {
		disposeTensor(output);
		throw new SileroVoiceActivityError("inference_failed");
	}
}

function disposeInferenceOutputs(value: unknown): void {
	if (!value || typeof value !== "object") return;
	try {
		disposeTensor((value as InferenceSession.ReturnType).output);
	} catch {
		// Malformed output cleanup is best effort.
	}
	try {
		disposeTensor((value as InferenceSession.ReturnType).stateN);
	} catch {
		// Malformed output cleanup is best effort.
	}
}

function releaseRawModel(raw: object, release: InferenceSession["release"]): Promise<void> {
	if (RELEASED_MODELS.has(raw)) return Promise.resolve();
	RELEASED_MODELS.add(raw);
	try {
		return Promise.resolve(Reflect.apply(release, raw, [])).then(
			() => undefined,
			() => undefined,
		);
	} catch {
		return Promise.resolve();
	}
}

function closeLateModel(pending: Promise<InferenceSession>): void {
	void pending.then(
		(raw) => {
			try {
				void captureModelSession(raw).release();
			} catch {
				// Invalid late models are released when they expose a usable release hook.
			}
		},
		() => undefined,
	);
}

function deadline(timeoutMs: number, code: "model_load_timeout" | "inference_timeout") {
	const controller = new AbortController();
	const timer = setTimeout(() => controller.abort(new SileroVoiceActivityError(code)), timeoutMs);
	return Object.freeze({
		signal: controller.signal,
		clear: () => clearTimeout(timer),
	});
}
