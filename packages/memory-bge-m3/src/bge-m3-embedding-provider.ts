import { Worker } from "node:worker_threads";
import type { MemoryEmbeddingProvider } from "@opengameagent/memory";
import { PendingRequestQueue } from "./pending-request-queue.js";
import { BGE_M3_DIMENSIONS, BgeM3EmbeddingError, type BgeM3EmbeddingProviderOptions } from "./types.js";
import { deriveBgeM3ModelId, type ValidatedBgeM3Configuration, validateBgeM3Options } from "./validation.js";
import type { BgeM3WorkerData, BgeM3WorkerRequest, BgeM3WorkerResponse } from "./worker-protocol.js";

interface PendingRequest {
	readonly requestId: number;
	readonly texts: readonly string[];
	readonly signal?: AbortSignal;
	readonly resolve: (vectors: readonly Float32Array[]) => void;
	readonly reject: (reason: unknown) => void;
	abortListener?: () => void;
	timeout?: NodeJS.Timeout;
	cancelled?: boolean;
}

export class BgeM3EmbeddingProvider implements MemoryEmbeddingProvider, AsyncDisposable {
	readonly modelId: string;
	readonly dimensions = BGE_M3_DIMENSIONS;
	readonly maxTextsPerRequest: number;

	private readonly configuration: ValidatedBgeM3Configuration;
	private readonly queue = new PendingRequestQueue<PendingRequest>();
	private worker: Worker | undefined;
	private workerTermination: Promise<void> | undefined;
	private workerReady = false;
	private active: PendingRequest | undefined;
	private nextRequestId = 1;
	private disposed = false;
	private terminalError: BgeM3EmbeddingError | undefined;

	constructor(options: BgeM3EmbeddingProviderOptions) {
		this.configuration = validateBgeM3Options(options);
		this.modelId = deriveBgeM3ModelId(this.configuration);
		this.maxTextsPerRequest = this.configuration.limits.maxTextsPerBatch;
	}

	async embed(texts: readonly string[], signal?: AbortSignal): Promise<readonly Float32Array[]> {
		const normalizedTexts = this.normalizeRequest(texts, signal);
		const pendingCount = this.queue.size + (this.active === undefined ? 0 : 1);
		if (pendingCount >= this.configuration.limits.maxPendingRequests) {
			throw new BgeM3EmbeddingError("request_capacity");
		}

		return await new Promise<readonly Float32Array[]>((resolve, reject) => {
			const request: PendingRequest = {
				requestId: this.nextRequestId,
				texts: normalizedTexts,
				...(signal === undefined ? {} : { signal }),
				resolve,
				reject,
			};
			this.nextRequestId = this.nextRequestId === Number.MAX_SAFE_INTEGER ? 1 : this.nextRequestId + 1;
			if (signal !== undefined) {
				request.abortListener = () => this.cancel(request, abortReason(signal));
				signal.addEventListener("abort", request.abortListener, { once: true });
			}
			this.queue.enqueue(request);
			this.pump();
		});
	}

	async dispose(): Promise<void> {
		if (this.disposed) return;
		this.disposed = true;
		const error = new BgeM3EmbeddingError("provider_disposed");
		if (this.active !== undefined) {
			this.rejectRequest(this.active, error);
			this.active = undefined;
		}
		for (const request of this.queue.drain()) this.rejectRequest(request, error);
		await this.terminateWorker();
	}

	async [Symbol.asyncDispose](): Promise<void> {
		await this.dispose();
	}

	private normalizeRequest(texts: readonly string[], signal: AbortSignal | undefined): readonly string[] {
		if (this.disposed) throw new BgeM3EmbeddingError("provider_disposed");
		if (this.terminalError !== undefined) throw this.terminalError;
		if (signal?.aborted === true) throw abortReason(signal);
		if (!Array.isArray(texts)) throw new BgeM3EmbeddingError("invalid_batch_size");
		const length = texts.length;
		if (length < 1 || length > this.maxTextsPerRequest) {
			throw new BgeM3EmbeddingError("invalid_batch_size");
		}
		const normalized: string[] = [];
		for (let index = 0; index < length; index += 1) {
			const text = texts[index];
			if (typeof text !== "string" || Buffer.byteLength(text, "utf8") > this.configuration.limits.maxTextBytes) {
				throw new BgeM3EmbeddingError("invalid_text_bytes");
			}
			normalized.push(text);
		}
		return normalized;
	}

	private pump(): void {
		if (this.disposed || this.workerTermination !== undefined || this.active !== undefined || this.queue.size === 0)
			return;
		const request = this.queue.dequeue();
		if (request === undefined) return;
		this.active = request;
		request.timeout = setTimeout(() => {
			this.cancel(request, new BgeM3EmbeddingError("request_timeout"));
		}, this.configuration.limits.requestTimeoutMs);
		if (this.worker === undefined) this.spawnWorker();
		if (this.workerReady) this.dispatch(request);
	}

	private spawnWorker(): void {
		const workerData: BgeM3WorkerData = { configuration: this.configuration };
		const execArgv = process.execArgv.filter((argument) => !argument.startsWith("--input-type"));
		const worker = new Worker(new URL("./bge-m3-worker.js", import.meta.url), { execArgv, workerData });
		this.worker = worker;
		this.workerReady = false;
		worker.on("message", (message: BgeM3WorkerResponse) => this.handleMessage(worker, message));
		worker.on("error", () => this.handleWorkerFailure(worker));
		worker.on("exit", (code) => {
			if (this.worker === worker && code !== 0) this.handleWorkerFailure(worker);
		});
	}

	private dispatch(request: PendingRequest): void {
		const message: BgeM3WorkerRequest = {
			kind: "embed",
			requestId: request.requestId,
			texts: request.texts,
		};
		this.worker?.postMessage(message);
	}

	private handleMessage(worker: Worker, message: BgeM3WorkerResponse): void {
		if (this.worker !== worker || this.disposed) return;
		if (message.kind === "ready") {
			this.workerReady = true;
			if (this.active?.cancelled) {
				this.active = undefined;
				this.pump();
			} else if (this.active !== undefined) {
				this.dispatch(this.active);
			}
			return;
		}
		if (message.kind === "failure" && message.terminal) {
			const error = new BgeM3EmbeddingError(message.code);
			this.terminalError = error;
			this.failAll(error);
			void this.terminateWorker();
			return;
		}

		const request = this.active;
		if (request === undefined || message.requestId !== request.requestId) return;
		if (request.cancelled) {
			this.active = undefined;
			this.pump();
			return;
		}
		if (message.kind === "failure") {
			const error = new BgeM3EmbeddingError(message.code);
			this.finishActive(error);
			return;
		}

		if (message.vectors.length !== request.texts.length) {
			this.finishActive(new BgeM3EmbeddingError("invalid_embedding"));
			return;
		}
		const vectors = message.vectors.map((buffer) => new Float32Array(buffer));
		if (vectors.some((vector) => vector.length !== this.dimensions)) {
			this.finishActive(new BgeM3EmbeddingError("invalid_embedding"));
			return;
		}
		this.resolveRequest(request, vectors);
		this.active = undefined;
		this.pump();
	}

	private handleWorkerFailure(worker: Worker): void {
		if (this.worker !== worker) return;
		this.worker = undefined;
		this.workerReady = false;
		if (this.active !== undefined) {
			if (!this.active.cancelled) {
				const error = new BgeM3EmbeddingError("worker_failure");
				this.rejectRequest(this.active, error);
			}
			this.active = undefined;
		}
		this.pump();
	}

	private cancel(request: PendingRequest, reason: unknown): void {
		if (this.active === request) {
			if (request.cancelled) return;
			request.cancelled = true;
			this.rejectRequest(request, reason);
			return;
		}
		if (!this.queue.remove(request)) return;
		this.rejectRequest(request, reason);
	}

	private finishActive(error: unknown): void {
		const request = this.active;
		if (request === undefined) return;
		this.rejectRequest(request, error);
		this.active = undefined;
		this.pump();
	}

	private failAll(error: unknown): void {
		if (this.active !== undefined) {
			this.rejectRequest(this.active, error);
			this.active = undefined;
		}
		for (const request of this.queue.drain()) this.rejectRequest(request, error);
	}

	private resolveRequest(request: PendingRequest, vectors: readonly Float32Array[]): void {
		this.clearRequestResources(request);
		request.resolve(vectors);
	}

	private rejectRequest(request: PendingRequest, error: unknown): void {
		this.clearRequestResources(request);
		request.reject(error);
	}

	private clearRequestResources(request: PendingRequest): void {
		if (request.timeout !== undefined) clearTimeout(request.timeout);
		if (request.signal !== undefined && request.abortListener !== undefined) {
			request.signal.removeEventListener("abort", request.abortListener);
		}
	}

	private terminateWorker(): Promise<void> {
		if (this.workerTermination !== undefined) return this.workerTermination;
		const worker = this.worker;
		this.worker = undefined;
		this.workerReady = false;
		if (worker === undefined) return Promise.resolve();
		const termination = worker.terminate().then(() => undefined);
		this.workerTermination = termination;
		return termination.finally(() => {
			if (this.workerTermination === termination) this.workerTermination = undefined;
		});
	}
}

function abortReason(signal: AbortSignal): unknown {
	return signal.reason ?? new DOMException("The operation was aborted", "AbortError");
}
