import {
	type MemoryEmbeddingProvider,
	MemoryValidationError,
	validateEmbeddingIdentity,
	validateVector,
} from "@opengameagent/memory";
import { embedMany, type EmbeddingModel } from "ai";

export interface AiSdkEmbeddingProviderOptions {
	readonly model: EmbeddingModel;
	readonly modelId: string;
	readonly dimensions: number;
	readonly maxTextsPerRequest: number;
	readonly maxTextBytes: number;
	readonly maxRetries: number;
	readonly maxParallelCalls: number;
}

export class AiSdkEmbeddingProvider implements MemoryEmbeddingProvider {
	readonly modelId: string;
	readonly dimensions: number;
	private readonly model: EmbeddingModel;
	readonly maxTextsPerRequest: number;
	private readonly maxTextBytes: number;
	private readonly maxRetries: number;
	private readonly maxParallelCalls: number;

	constructor(options: AiSdkEmbeddingProviderOptions) {
		const model = options.model;
		const modelId = options.modelId;
		const dimensions = options.dimensions;
		const maxTextsPerRequest = options.maxTextsPerRequest;
		const maxTextBytes = options.maxTextBytes;
		const maxRetries = options.maxRetries;
		const maxParallelCalls = options.maxParallelCalls;
		validateEmbeddingIdentity(modelId, dimensions);
		validatePositiveSafeInteger(maxTextsPerRequest, "max_texts_per_request");
		validatePositiveSafeInteger(maxTextBytes, "max_text_bytes");
		validateNonNegativeSafeInteger(maxRetries, "max_retries");
		validatePositiveSafeInteger(maxParallelCalls, "max_parallel_calls");
		this.model = model;
		this.modelId = modelId;
		this.dimensions = dimensions;
		this.maxTextsPerRequest = maxTextsPerRequest;
		this.maxTextBytes = maxTextBytes;
		this.maxRetries = maxRetries;
		this.maxParallelCalls = maxParallelCalls;
	}

	async embed(texts: readonly string[], signal?: AbortSignal): Promise<readonly Float32Array[]> {
		throwIfAborted(signal);
		const values = normalizeTexts(texts, this.maxTextsPerRequest, this.maxTextBytes);

		const result = await embedMany({
			model: this.model,
			values,
			maxRetries: this.maxRetries,
			maxParallelCalls: this.maxParallelCalls,
			...(signal === undefined ? {} : { abortSignal: signal }),
			telemetry: { isEnabled: false },
		});
		throwIfAborted(signal);
		const embeddings = result.embeddings;
		if (!Array.isArray(embeddings) || embeddings.length !== values.length) {
			throw new TypeError("Embedding provider returned an invalid batch cardinality.");
		}
		const vectors: Float32Array[] = [];
		for (let index = 0; index < values.length; index += 1) {
			const source = embeddings[index];
			if (!Array.isArray(source) || source.length !== this.dimensions) {
				throw new MemoryValidationError("embedding_dimensions");
			}
			const vector = new Float32Array(this.dimensions);
			for (let dimension = 0; dimension < this.dimensions; dimension += 1) {
				vector[dimension] = source[dimension] as number;
			}
			validateVector(vector, this.dimensions);
			vectors.push(vector);
		}
		return vectors;
	}
}

function normalizeTexts(texts: readonly string[], maximumCount: number, maximumBytes: number): string[] {
	if (!Array.isArray(texts)) throw new MemoryValidationError("invalid_embedding_batch_size");
	const length = texts.length;
	if (length < 1 || length > maximumCount) throw new MemoryValidationError("invalid_embedding_batch_size");
	const normalized: string[] = [];
	for (let index = 0; index < length; index += 1) {
		const text = texts[index];
		if (typeof text !== "string") throw new MemoryValidationError("invalid_embedding_text_bytes");
		const bytes = Buffer.byteLength(text, "utf8");
		if (bytes < 1 || bytes > maximumBytes) {
			throw new MemoryValidationError("invalid_embedding_text_bytes");
		}
		normalized.push(text);
	}
	return normalized;
}

function validatePositiveSafeInteger(value: number, name: string): void {
	if (!Number.isSafeInteger(value) || value < 1) throw new MemoryValidationError(`invalid_${name}`);
}

function validateNonNegativeSafeInteger(value: number, name: string): void {
	if (!Number.isSafeInteger(value) || value < 0) throw new MemoryValidationError(`invalid_${name}`);
}

function throwIfAborted(signal: AbortSignal | undefined): void {
	if (signal?.aborted) throw signal.reason;
}
