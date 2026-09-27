import { createHash } from "node:crypto";
import { isAbsolute, resolve } from "node:path";
import { BGE_M3_MAX_TOKENS, BgeM3EmbeddingError, type BgeM3EmbeddingProviderOptions } from "./types.js";

const SHA256_PATTERN = /^[a-f\d]{64}$/i;
const MIN_TOKENS_WITH_SPECIAL_TOKENS = 2;
const NODE_MAX_TIMER_DELAY_MS = 2_147_483_647;

export interface ValidatedBgeM3Configuration {
	readonly artifacts: {
		readonly model: { readonly path: string; readonly sha256: string };
		readonly tokenizer: { readonly path: string; readonly sha256: string };
		readonly tokenizerConfig: { readonly path: string; readonly sha256: string };
		readonly modelConfig: { readonly path: string; readonly sha256: string };
	};
	readonly limits: BgeM3EmbeddingProviderOptions["limits"];
}

export function validateBgeM3Options(options: BgeM3EmbeddingProviderOptions): ValidatedBgeM3Configuration {
	const sourceArtifacts = options.artifacts;
	const sourceLimits = options.limits;
	const artifacts = {
		model: validateArtifact(sourceArtifacts.model),
		tokenizer: validateArtifact(sourceArtifacts.tokenizer),
		tokenizerConfig: validateArtifact(sourceArtifacts.tokenizerConfig),
		modelConfig: validateArtifact(sourceArtifacts.modelConfig),
	};
	const limits = {
		maxTextsPerBatch: sourceLimits.maxTextsPerBatch,
		maxTextBytes: sourceLimits.maxTextBytes,
		maxTokens: sourceLimits.maxTokens,
		requestTimeoutMs: sourceLimits.requestTimeoutMs,
		maxPendingRequests: sourceLimits.maxPendingRequests,
	};
	assertPositiveInteger(limits.maxTextsPerBatch);
	assertPositiveInteger(limits.maxTextBytes);
	assertPositiveInteger(limits.requestTimeoutMs);
	assertPositiveInteger(limits.maxPendingRequests);
	if (limits.requestTimeoutMs > NODE_MAX_TIMER_DELAY_MS) {
		throw new BgeM3EmbeddingError("invalid_configuration");
	}
	if (
		!Number.isSafeInteger(limits.maxTokens) ||
		limits.maxTokens < MIN_TOKENS_WITH_SPECIAL_TOKENS ||
		limits.maxTokens > BGE_M3_MAX_TOKENS
	) {
		throw new BgeM3EmbeddingError("invalid_configuration");
	}
	return { artifacts, limits };
}

export function deriveBgeM3ModelId(configuration: ValidatedBgeM3Configuration): string {
	const identity = createHash("sha256")
		.update("bge-m3-local-v1\0")
		.update(configuration.artifacts.model.sha256)
		.update(configuration.artifacts.tokenizer.sha256)
		.update(configuration.artifacts.tokenizerConfig.sha256)
		.update(configuration.artifacts.modelConfig.sha256)
		.update(String(configuration.limits.maxTokens))
		.digest("hex");
	return `bge-m3-local:${identity}`;
}

function validateArtifact(artifact: { readonly path: string; readonly sha256: string }) {
	const sourcePath = artifact.path;
	const sourceSha256 = artifact.sha256;
	if (sourcePath.length === 0 || !SHA256_PATTERN.test(sourceSha256)) {
		throw new BgeM3EmbeddingError("invalid_configuration");
	}
	const path = isAbsolute(sourcePath) ? resolve(sourcePath) : resolve(process.cwd(), sourcePath);
	return { path, sha256: sourceSha256.toLowerCase() };
}

function assertPositiveInteger(value: number): void {
	if (!Number.isSafeInteger(value) || value < 1) {
		throw new BgeM3EmbeddingError("invalid_configuration");
	}
}
