export const BGE_M3_DIMENSIONS = 1024;
export const BGE_M3_MAX_TOKENS = 8192;

export interface BgeM3Artifact {
	readonly path: string;
	readonly sha256: string;
}

export interface BgeM3Artifacts {
	readonly model: BgeM3Artifact;
	readonly tokenizer: BgeM3Artifact;
	readonly tokenizerConfig: BgeM3Artifact;
	readonly modelConfig: BgeM3Artifact;
}

export interface BgeM3Limits {
	readonly maxTextsPerBatch: number;
	readonly maxTextBytes: number;
	readonly maxTokens: number;
	readonly requestTimeoutMs: number;
	readonly maxPendingRequests: number;
}

export interface BgeM3EmbeddingProviderOptions {
	readonly artifacts: BgeM3Artifacts;
	readonly limits: BgeM3Limits;
}

export type BgeM3EmbeddingErrorCode =
	| "invalid_artifact"
	| "invalid_batch_size"
	| "invalid_configuration"
	| "invalid_embedding"
	| "invalid_text_bytes"
	| "provider_disposed"
	| "request_capacity"
	| "request_timeout"
	| "worker_failure";

export class BgeM3EmbeddingError extends Error {
	override readonly name = "BgeM3EmbeddingError";

	constructor(
		readonly code: BgeM3EmbeddingErrorCode,
		options?: ErrorOptions,
	) {
		super(`bge_m3_${code}`, options);
	}
}
