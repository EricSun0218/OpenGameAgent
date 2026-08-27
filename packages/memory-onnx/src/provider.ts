import { createHash } from "node:crypto";
import { createReadStream } from "node:fs";
import { lstat, readFile, realpath, stat } from "node:fs/promises";
import { isAbsolute, relative, resolve } from "node:path";
import { Worker } from "node:worker_threads";
import type { GameMemoryEmbeddingIdentity, GameMemoryEmbeddingProvider } from "@opengameagent/memory";

const DIMENSIONS = 1_024;
const MODEL_FILES = [
	"config.json",
	"tokenizer_config.json",
	"tokenizer.json",
	"sentencepiece.bpe.model",
	"onnx/model_int8.onnx",
] as const;

export type BgeM3ModelFile = (typeof MODEL_FILES)[number];

export interface BgeM3FileIntegrity {
	sha256?: string;
	minimumBytes?: number;
	maximumBytes?: number;
}

export interface BgeM3ModelManifest {
	modelVersion: string;
	files?: Partial<Record<BgeM3ModelFile, BgeM3FileIntegrity>>;
}

export type BgeM3EmbeddingFailure =
	| "cancelled"
	| "disposed"
	| "integrity"
	| "invalid-input"
	| "load"
	| "queue-full"
	| "timeout"
	| "tokenization"
	| "inference"
	| "invalid-output";

export interface BgeM3EmbeddingMetrics {
	mode: "query" | "document";
	batchSize: number;
	queueMilliseconds: number;
	loadMilliseconds: number;
	tokenizationMilliseconds: number;
	inferenceMilliseconds: number;
	truncatedInputs: number;
	failure?: BgeM3EmbeddingFailure;
}

export interface BgeM3EmbeddingBackendResult {
	vectors: readonly Float32Array[];
	loadMilliseconds: number;
	tokenizationMilliseconds: number;
	inferenceMilliseconds: number;
	truncatedInputs: number;
}

export interface BgeM3EmbeddingBackend extends AsyncDisposable {
	embed(texts: readonly string[], maximumTokens: number, signal: AbortSignal): Promise<BgeM3EmbeddingBackendResult>;
}

export interface BgeM3EmbeddingBackendConfiguration {
	modelDirectory: string;
	dimensions: number;
}

export type BgeM3EmbeddingBackendFactory = (configuration: BgeM3EmbeddingBackendConfiguration) => BgeM3EmbeddingBackend;

export interface BgeM3OnnxEmbeddingProviderOptions {
	modelDirectory: string;
	manifest: BgeM3ModelManifest;
	maximumTokens?: number;
	maximumBatchSize?: number;
	maximumDocumentsPerRequest?: number;
	maximumCharactersPerDocument?: number;
	maximumTotalCharacters?: number;
	maximumQueuedBatches?: number;
	maximumResidentModelBytes?: number;
	concurrency?: number;
	timeoutMilliseconds?: number;
	onMetrics?: (metrics: BgeM3EmbeddingMetrics) => void;
	backendFactory?: BgeM3EmbeddingBackendFactory;
}

export interface ValidatedBgeM3Model {
	directory: string;
	files: Readonly<Record<BgeM3ModelFile, { bytes: number; sha256?: string }>>;
}

interface QueuedBatch {
	texts: readonly string[];
	mode: "query" | "document";
	enqueuedAt: number;
	signal?: AbortSignal;
	resolve: (result: readonly Float32Array[]) => void;
	reject: (reason: unknown) => void;
}

interface WorkerResponse {
	id: number;
	ok: boolean;
	vectors?: ArrayBuffer[];
	loadMilliseconds?: number;
	tokenizationMilliseconds?: number;
	inferenceMilliseconds?: number;
	truncatedInputs?: number;
	failure?: BgeM3EmbeddingFailure;
}

class BgeM3BackendError extends Error {
	constructor(readonly failure: BgeM3EmbeddingFailure) {
		super(`BGE-M3 worker failed: ${failure}`);
	}
}

function abortError(message = "Embedding request was cancelled."): Error {
	return new DOMException(message, "AbortError");
}

async function waitForSharedLoad<T>(promise: Promise<T>, signal?: AbortSignal): Promise<T> {
	if (!signal) return await promise;
	if (signal.aborted) throw abortError();
	return await new Promise<T>((resolvePromise, rejectPromise) => {
		const onAbort = (): void => rejectPromise(abortError());
		signal.addEventListener("abort", onAbort, { once: true });
		promise.then(resolvePromise, rejectPromise).finally(() => signal.removeEventListener("abort", onAbort));
	});
}

function assertInteger(name: string, value: number, minimum: number, maximum: number): number {
	if (!Number.isInteger(value) || value < minimum || value > maximum) {
		throw new RangeError(`${name} must be an integer from ${minimum} through ${maximum}.`);
	}
	return value;
}

function inside(root: string, child: string): boolean {
	const path = relative(root, child);
	return path === "" || (!path.startsWith("..") && !isAbsolute(path));
}

async function readBoundedJson(path: string, maximumBytes: number): Promise<Record<string, unknown>> {
	const metadata = await stat(path);
	if (metadata.size < 2 || metadata.size > maximumBytes) throw new Error("A model metadata file has an invalid size.");
	let value: unknown;
	try {
		value = JSON.parse(await readFile(path, "utf8"));
	} catch {
		throw new Error("A model metadata file is not valid JSON.");
	}
	if (!value || typeof value !== "object" || Array.isArray(value)) {
		throw new Error("A model metadata file must contain a JSON object.");
	}
	return value as Record<string, unknown>;
}

async function sha256(path: string, signal?: AbortSignal): Promise<string> {
	if (signal?.aborted) throw abortError();
	const hash = createHash("sha256");
	const stream = createReadStream(path);
	const onAbort = (): void => {
		stream.destroy(abortError());
	};
	signal?.addEventListener("abort", onAbort, { once: true });
	try {
		for await (const chunk of stream) hash.update(chunk as Buffer);
		return hash.digest("hex");
	} finally {
		signal?.removeEventListener("abort", onAbort);
	}
}

function validateIntegrityRule(rule: BgeM3FileIntegrity | undefined): void {
	if (!rule) return;
	if (rule.sha256 !== undefined && !/^[a-f0-9]{64}$/i.test(rule.sha256)) {
		throw new TypeError("A model SHA-256 value is invalid.");
	}
	if (rule.minimumBytes !== undefined) assertInteger("minimumBytes", rule.minimumBytes, 1, 3_000_000_000);
	if (rule.maximumBytes !== undefined) assertInteger("maximumBytes", rule.maximumBytes, 1, 3_000_000_000);
	if (rule.minimumBytes !== undefined && rule.maximumBytes !== undefined && rule.minimumBytes > rule.maximumBytes) {
		throw new RangeError("A model file minimum size exceeds its maximum size.");
	}
}

export async function validateBgeM3ModelDirectory(
	modelDirectory: string,
	manifest: BgeM3ModelManifest,
	signal?: AbortSignal,
): Promise<ValidatedBgeM3Model> {
	if (!modelDirectory.trim()) throw new TypeError("modelDirectory is required.");
	if (!manifest.modelVersion.trim() || manifest.modelVersion.length > 128) {
		throw new TypeError("manifest.modelVersion is required and must be at most 128 characters.");
	}
	for (const rule of Object.values(manifest.files ?? {})) validateIntegrityRule(rule);
	if (signal?.aborted) throw abortError();

	const requestedRoot = resolve(modelDirectory);
	const rootMetadata = await lstat(requestedRoot);
	if (!rootMetadata.isDirectory() || rootMetadata.isSymbolicLink()) {
		throw new Error("The embedding model root must be a real directory.");
	}
	const root = await realpath(requestedRoot);
	const onnxDirectory = await lstat(resolve(root, "onnx"));
	if (!onnxDirectory.isDirectory() || onnxDirectory.isSymbolicLink()) {
		throw new Error("The embedding model ONNX directory must be a real directory.");
	}
	const validated = {} as Record<BgeM3ModelFile, { bytes: number; sha256?: string }>;
	const defaultMaximum: Record<BgeM3ModelFile, number> = {
		"config.json": 1_048_576,
		"tokenizer_config.json": 2_097_152,
		"tokenizer.json": 67_108_864,
		"sentencepiece.bpe.model": 67_108_864,
		"onnx/model_int8.onnx": 3_000_000_000,
	};

	for (const file of MODEL_FILES) {
		if (signal?.aborted) throw abortError();
		const requestedPath = resolve(root, file);
		if (!inside(root, requestedPath)) throw new Error("A model file escapes the model root.");
		const fileMetadata = await lstat(requestedPath);
		if (!fileMetadata.isFile() || fileMetadata.isSymbolicLink()) {
			throw new Error(`Required model file is not a regular file: ${file}`);
		}
		const resolvedPath = await realpath(requestedPath);
		if (!inside(root, resolvedPath)) throw new Error(`Required model file escapes the model root: ${file}`);
		const rule = manifest.files?.[file];
		const minimum = rule?.minimumBytes ?? 1;
		const maximum = rule?.maximumBytes ?? defaultMaximum[file];
		if (fileMetadata.size < minimum || fileMetadata.size > maximum) {
			throw new Error(`Required model file has an invalid size: ${file}`);
		}
		const digest = rule?.sha256 ? await sha256(resolvedPath, signal) : undefined;
		if (rule?.sha256 && digest?.toLowerCase() !== rule.sha256.toLowerCase()) {
			throw new Error(`Required model file failed its SHA-256 check: ${file}`);
		}
		validated[file] = digest ? { bytes: fileMetadata.size, sha256: digest } : { bytes: fileMetadata.size };
	}

	const config = await readBoundedJson(resolve(root, "config.json"), defaultMaximum["config.json"]);
	if (
		config["model_type"] !== "xlm-roberta" ||
		config["hidden_size"] !== DIMENSIONS ||
		typeof config["max_position_embeddings"] !== "number" ||
		config["max_position_embeddings"] < 8_192
	) {
		throw new Error("The supplied model is not a supported 1024-dimensional BGE-M3 model.");
	}
	const tokenizer = await readBoundedJson(
		resolve(root, "tokenizer_config.json"),
		defaultMaximum["tokenizer_config.json"],
	);
	const tokenizerClass = tokenizer["tokenizer_class"];
	if (
		(tokenizerClass !== "XLMRobertaTokenizer" && tokenizerClass !== "XLMRobertaTokenizerFast") ||
		typeof tokenizer["model_max_length"] !== "number" ||
		tokenizer["model_max_length"] < 8_192
	) {
		throw new Error("The supplied tokenizer is not compatible with BGE-M3.");
	}
	return { directory: root, files: validated };
}

function normalize(vector: Float32Array): Float32Array {
	if (vector.length !== DIMENSIONS) throw new Error("The embedding backend returned an invalid vector size.");
	let squared = 0;
	for (const value of vector) {
		if (!Number.isFinite(value)) throw new Error("The embedding backend returned a non-finite value.");
		squared += value * value;
	}
	if (!(squared > 0)) throw new Error("The embedding backend returned a zero vector.");
	const scale = 1 / Math.sqrt(squared);
	return Float32Array.from(vector, (value) => value * scale);
}

class WorkerBgeM3EmbeddingBackend implements BgeM3EmbeddingBackend {
	private worker: Worker | undefined;
	private nextId = 1;
	private disposed = false;

	constructor(private readonly configuration: BgeM3EmbeddingBackendConfiguration) {}

	private getWorker(): Worker {
		if (this.disposed) throw new Error("The embedding backend is disposed.");
		if (!this.worker) {
			this.worker = new Worker(new URL("./worker.js", import.meta.url), { workerData: this.configuration });
		}
		return this.worker;
	}

	private async discardWorker(): Promise<void> {
		const worker = this.worker;
		this.worker = undefined;
		if (worker) await worker.terminate();
	}

	async embed(
		texts: readonly string[],
		maximumTokens: number,
		signal: AbortSignal,
	): Promise<BgeM3EmbeddingBackendResult> {
		if (signal.aborted) throw abortError();
		const worker = this.getWorker();
		const id = this.nextId++;
		return await new Promise<BgeM3EmbeddingBackendResult>((resolvePromise, rejectPromise) => {
			let settled = false;
			const cleanup = (): void => {
				worker.off("message", onMessage);
				worker.off("error", onError);
				worker.off("exit", onExit);
				signal.removeEventListener("abort", onAbort);
			};
			const fail = (reason: unknown): void => {
				if (settled) return;
				settled = true;
				cleanup();
				rejectPromise(reason);
			};
			const onMessage = (response: WorkerResponse): void => {
				if (response.id !== id || settled) return;
				settled = true;
				cleanup();
				if (!response.ok || !response.vectors) {
					this.worker = undefined;
					void worker.terminate();
					rejectPromise(new BgeM3BackendError(response.failure ?? "inference"));
					return;
				}
				resolvePromise({
					vectors: response.vectors.map((buffer) => new Float32Array(buffer)),
					loadMilliseconds: response.loadMilliseconds ?? 0,
					tokenizationMilliseconds: response.tokenizationMilliseconds ?? 0,
					inferenceMilliseconds: response.inferenceMilliseconds ?? 0,
					truncatedInputs: response.truncatedInputs ?? 0,
				});
			};
			const onError = (): void => {
				void this.discardWorker();
				fail(new Error("BGE-M3 worker failed."));
			};
			const onExit = (): void => {
				this.worker = undefined;
				fail(new Error("BGE-M3 worker exited before completing the request."));
			};
			const onAbort = (): void => {
				void this.discardWorker();
				fail(abortError());
			};
			worker.on("message", onMessage);
			worker.once("error", onError);
			worker.once("exit", onExit);
			signal.addEventListener("abort", onAbort, { once: true });
			worker.postMessage({ type: "embed", id, texts: [...texts], maximumTokens });
		});
	}

	async [Symbol.asyncDispose](): Promise<void> {
		if (this.disposed) return;
		this.disposed = true;
		await this.discardWorker();
	}
}

export class BgeM3OnnxEmbeddingProvider implements GameMemoryEmbeddingProvider, AsyncDisposable {
	readonly identity: GameMemoryEmbeddingIdentity;
	private readonly modelDirectory: string;
	private readonly manifest: BgeM3ModelManifest;
	private readonly maximumTokens: number;
	private readonly maximumBatchSize: number;
	private readonly maximumDocumentsPerRequest: number;
	private readonly maximumCharactersPerDocument: number;
	private readonly maximumTotalCharacters: number;
	private readonly maximumQueuedBatches: number;
	private readonly maximumResidentModelBytes: number;
	private readonly timeoutMilliseconds: number;
	private readonly onMetrics: ((metrics: BgeM3EmbeddingMetrics) => void) | undefined;
	private readonly backends: { backend: BgeM3EmbeddingBackend; busy: boolean }[];
	private readonly queue: QueuedBatch[] = [];
	private readonly lifecycle = new AbortController();
	private readyPromise: Promise<ValidatedBgeM3Model> | undefined;
	private disposed = false;

	constructor(options: BgeM3OnnxEmbeddingProviderOptions) {
		this.modelDirectory = options.modelDirectory;
		this.manifest = options.manifest;
		this.maximumTokens = assertInteger("maximumTokens", options.maximumTokens ?? 8_192, 8, 8_192);
		this.maximumBatchSize = assertInteger("maximumBatchSize", options.maximumBatchSize ?? 8, 1, 64);
		this.maximumDocumentsPerRequest = assertInteger(
			"maximumDocumentsPerRequest",
			options.maximumDocumentsPerRequest ?? 256,
			1,
			4_096,
		);
		this.maximumCharactersPerDocument = assertInteger(
			"maximumCharactersPerDocument",
			options.maximumCharactersPerDocument ?? 32_768,
			1,
			1_048_576,
		);
		this.maximumTotalCharacters = assertInteger(
			"maximumTotalCharacters",
			options.maximumTotalCharacters ?? 262_144,
			1,
			16_777_216,
		);
		this.maximumQueuedBatches = assertInteger("maximumQueuedBatches", options.maximumQueuedBatches ?? 64, 0, 4_096);
		this.maximumResidentModelBytes = assertInteger(
			"maximumResidentModelBytes",
			options.maximumResidentModelBytes ?? 1_610_612_736,
			1_048_576,
			12_000_000_000,
		);
		this.timeoutMilliseconds = assertInteger(
			"timeoutMilliseconds",
			options.timeoutMilliseconds ?? 60_000,
			100,
			600_000,
		);
		const concurrency = assertInteger("concurrency", options.concurrency ?? 1, 1, 4);
		this.onMetrics = options.onMetrics;
		const factory = options.backendFactory ?? ((configuration) => new WorkerBgeM3EmbeddingBackend(configuration));
		this.backends = Array.from({ length: concurrency }, () => ({
			backend: factory({ modelDirectory: resolve(options.modelDirectory), dimensions: DIMENSIONS }),
			busy: false,
		}));
		if (!options.manifest.modelVersion.trim() || options.manifest.modelVersion.length > 128) {
			throw new TypeError("manifest.modelVersion is required and must be at most 128 characters.");
		}
		this.identity = {
			model: "bge-m3-int8-onnx",
			version: options.manifest.modelVersion,
			dimensions: DIMENSIONS,
			preprocessing: `xlm-roberta:mean-pool:l2:max-${this.maximumTokens}`,
		};
	}

	async load(signal?: AbortSignal): Promise<void> {
		if (this.disposed) throw new Error("The embedding provider is disposed.");
		if (!this.readyPromise) {
			const pending = validateBgeM3ModelDirectory(this.modelDirectory, this.manifest, this.lifecycle.signal).then(
				(validated) => {
					const residentModelBytes = validated.files["onnx/model_int8.onnx"].bytes * this.backends.length;
					if (residentModelBytes > this.maximumResidentModelBytes) {
						throw new Error("The configured BGE-M3 worker pool exceeds the resident model memory bound.");
					}
					return validated;
				},
			);
			const guarded = pending.catch((error: unknown) => {
				if (this.readyPromise === guarded) this.readyPromise = undefined;
				throw error;
			});
			this.readyPromise = guarded;
		}
		await waitForSharedLoad(this.readyPromise, signal);
	}

	async embedQuery(text: string, signal?: AbortSignal): Promise<Float32Array> {
		const [vector] = await this.embedMany([text], "query", signal);
		if (!vector) throw new Error("The embedding backend returned no query vector.");
		return vector;
	}

	async embedDocuments(texts: readonly string[], signal?: AbortSignal): Promise<readonly Float32Array[]> {
		return await this.embedMany(texts, "document", signal);
	}

	private async embedMany(
		texts: readonly string[],
		mode: "query" | "document",
		signal?: AbortSignal,
	): Promise<readonly Float32Array[]> {
		if (this.disposed) throw new Error("The embedding provider is disposed.");
		if (signal?.aborted) throw abortError();
		if (texts.length < 1 || texts.length > this.maximumDocumentsPerRequest) {
			throw new RangeError("The embedding document count is outside the configured bound.");
		}
		let totalCharacters = 0;
		for (const text of texts) {
			if (!text || text.length > this.maximumCharactersPerDocument) {
				throw new RangeError("An embedding document is empty or exceeds the configured character bound.");
			}
			totalCharacters += text.length;
		}
		if (totalCharacters > this.maximumTotalCharacters) {
			throw new RangeError("The embedding request exceeds the configured total character bound.");
		}
		try {
			await this.load(signal);
		} catch (error) {
			this.emit({
				mode,
				batchSize: texts.length,
				queueMilliseconds: 0,
				loadMilliseconds: 0,
				tokenizationMilliseconds: 0,
				inferenceMilliseconds: 0,
				truncatedInputs: 0,
				failure: signal?.aborted ? "cancelled" : "integrity",
			});
			throw error;
		}
		const chunks: Promise<readonly Float32Array[]>[] = [];
		for (let offset = 0; offset < texts.length; offset += this.maximumBatchSize) {
			chunks.push(this.enqueue(texts.slice(offset, offset + this.maximumBatchSize), mode, signal));
		}
		return (await Promise.all(chunks)).flat();
	}

	private enqueue(
		texts: readonly string[],
		mode: "query" | "document",
		signal?: AbortSignal,
	): Promise<readonly Float32Array[]> {
		const idle = this.backends.some((entry) => !entry.busy);
		if (!idle && this.queue.length >= this.maximumQueuedBatches) {
			this.emit({
				mode,
				batchSize: texts.length,
				queueMilliseconds: 0,
				loadMilliseconds: 0,
				tokenizationMilliseconds: 0,
				inferenceMilliseconds: 0,
				truncatedInputs: 0,
				failure: "queue-full",
			});
			throw new Error("The BGE-M3 embedding queue is full.");
		}
		return new Promise<readonly Float32Array[]>((resolvePromise, rejectPromise) => {
			const queued: QueuedBatch = {
				texts,
				mode,
				enqueuedAt: performance.now(),
				...(signal ? { signal } : {}),
				resolve: resolvePromise,
				reject: rejectPromise,
			};
			this.queue.push(queued);
			this.pump();
		});
	}

	private pump(): void {
		if (this.disposed) return;
		for (const slot of this.backends) {
			if (slot.busy) continue;
			let batch = this.queue.shift();
			while (batch?.signal?.aborted) {
				batch.reject(abortError());
				batch = this.queue.shift();
			}
			if (!batch) return;
			slot.busy = true;
			void this.runBatch(slot, batch).finally(() => {
				slot.busy = false;
				this.pump();
			});
		}
	}

	private async runBatch(slot: { backend: BgeM3EmbeddingBackend; busy: boolean }, batch: QueuedBatch): Promise<void> {
		const queueMilliseconds = performance.now() - batch.enqueuedAt;
		const controller = new AbortController();
		let timedOut = false;
		const onAbort = (): void => controller.abort();
		batch.signal?.addEventListener("abort", onAbort, { once: true });
		const timeout = setTimeout(() => {
			timedOut = true;
			controller.abort();
		}, this.timeoutMilliseconds);
		try {
			const result = await slot.backend.embed(batch.texts, this.maximumTokens, controller.signal);
			const vectors = result.vectors.map(normalize);
			if (vectors.length !== batch.texts.length)
				throw new Error("The embedding backend returned the wrong batch size.");
			this.emit({
				mode: batch.mode,
				batchSize: batch.texts.length,
				queueMilliseconds,
				loadMilliseconds: result.loadMilliseconds,
				tokenizationMilliseconds: result.tokenizationMilliseconds,
				inferenceMilliseconds: result.inferenceMilliseconds,
				truncatedInputs: result.truncatedInputs,
			});
			batch.resolve(vectors);
		} catch (error) {
			const failure: BgeM3EmbeddingFailure = this.disposed
				? "disposed"
				: timedOut
					? "timeout"
					: batch.signal?.aborted
						? "cancelled"
						: error instanceof BgeM3BackendError
							? error.failure
							: "inference";
			this.emit({
				mode: batch.mode,
				batchSize: batch.texts.length,
				queueMilliseconds,
				loadMilliseconds: 0,
				tokenizationMilliseconds: 0,
				inferenceMilliseconds: 0,
				truncatedInputs: 0,
				failure,
			});
			batch.reject(failure === "cancelled" ? abortError() : new Error(`BGE-M3 embedding failed: ${failure}`));
		} finally {
			clearTimeout(timeout);
			batch.signal?.removeEventListener("abort", onAbort);
		}
	}

	private emit(metrics: BgeM3EmbeddingMetrics): void {
		try {
			this.onMetrics?.(metrics);
		} catch {
			// Metrics observers cannot affect inference.
		}
	}

	async [Symbol.asyncDispose](): Promise<void> {
		if (this.disposed) return;
		this.disposed = true;
		this.lifecycle.abort();
		for (const batch of this.queue.splice(0)) batch.reject(new Error("The embedding provider is disposed."));
		await Promise.allSettled(this.backends.map(({ backend }) => backend[Symbol.asyncDispose]()));
	}
}
