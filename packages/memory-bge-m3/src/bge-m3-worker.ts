import { Tokenizer } from "@huggingface/tokenizers";
import { parentPort, workerData } from "node:worker_threads";
import * as ort from "onnxruntime-web";
import { normalizeDenseVector } from "./dense-normalization.js";
import { BgeM3ModelOutputError, classifyBgeM3RequestFailure } from "./embedding-failure.js";
import { PinnedArtifactError, readPinnedArtifact } from "./pinned-artifact.js";
import { BGE_M3_DIMENSIONS, BGE_M3_MAX_TOKENS } from "./types.js";
import type { ValidatedBgeM3Configuration } from "./validation.js";
import type { BgeM3WorkerData, BgeM3WorkerRequest, BgeM3WorkerResponse } from "./worker-protocol.js";

const INPUT_IDS = "input_ids";
const ATTENTION_MASK = "attention_mask";
const OUTPUT = "last_hidden_state";
const CLS_TOKEN_ID = 0;
const PAD_TOKEN_ID = 1;
const EOS_TOKEN_ID = 2;
const NODE_WASM_THREADS = 1;

const port = parentPort;
if (port === null) throw new Error("bge_m3_worker_failure");
const configuration = (workerData as BgeM3WorkerData).configuration;

void initialize(configuration)
	.then(({ session, tokenizer }) => {
		post({ kind: "ready" });
		port.on("message", (request: BgeM3WorkerRequest) => {
			if (request.kind !== "embed") return;
			void embed(session, tokenizer, configuration, request)
				.then((vectors) => {
					post({ kind: "result", requestId: request.requestId, vectors }, vectors);
				})
				.catch((error: unknown) => {
					post({
						kind: "failure",
						requestId: request.requestId,
						code: classifyBgeM3RequestFailure(error),
						terminal: false,
					});
				});
		});
	})
	.catch((error: unknown) => {
		post({ kind: "failure", code: classifyInitializationFailure(error), terminal: true });
	});

async function initialize(config: ValidatedBgeM3Configuration): Promise<{
	readonly session: ort.InferenceSession;
	readonly tokenizer: Tokenizer;
}> {
	const [model, modelConfigBytes, tokenizerConfigBytes, tokenizerBytes] = await Promise.all([
		readPinnedArtifact(config.artifacts.model),
		readPinnedArtifact(config.artifacts.modelConfig),
		readPinnedArtifact(config.artifacts.tokenizerConfig),
		readPinnedArtifact(config.artifacts.tokenizer),
	]);
	const modelConfig = readJson(modelConfigBytes);
	const tokenizerConfig = readJson(tokenizerConfigBytes);
	const tokenizerJson = readJson(tokenizerBytes);
	validateModelConfig(modelConfig);
	validateTokenizerConfig(tokenizerConfig);
	const tokenizer = new Tokenizer(asObject(tokenizerJson), asObject(tokenizerConfig));
	if (
		tokenizer.token_to_id("<s>") !== CLS_TOKEN_ID ||
		tokenizer.token_to_id("<pad>") !== PAD_TOKEN_ID ||
		tokenizer.token_to_id("</s>") !== EOS_TOKEN_ID
	) {
		throw new ConfigurationFailure();
	}

	ort.env.wasm.numThreads = NODE_WASM_THREADS;
	ort.env.wasm.proxy = false;
	const session = await ort.InferenceSession.create(model, { executionProviders: ["wasm"] });
	validateSession(session);
	return { session, tokenizer };
}

async function embed(
	session: ort.InferenceSession,
	tokenizer: Tokenizer,
	config: ValidatedBgeM3Configuration,
	request: BgeM3WorkerRequest,
): Promise<readonly ArrayBuffer[]> {
	const encoded = request.texts.map((text) => tokenizer.encode(text, { add_special_tokens: true }).ids);
	const truncated = encoded.map((ids) => truncate(ids, config.limits.maxTokens));
	let sequenceLength = 0;
	for (const ids of truncated) sequenceLength = Math.max(sequenceLength, ids.length);
	const elementCount = request.texts.length * sequenceLength;
	const inputIds = new BigInt64Array(elementCount);
	const attentionMask = new BigInt64Array(elementCount);
	inputIds.fill(BigInt(PAD_TOKEN_ID));
	for (let row = 0; row < truncated.length; row += 1) {
		const ids = truncated[row];
		if (ids === undefined) throw new BgeM3ModelOutputError();
		for (let column = 0; column < ids.length; column += 1) {
			const tokenId = ids[column];
			if (tokenId === undefined || !Number.isSafeInteger(tokenId) || tokenId < 0) {
				throw new BgeM3ModelOutputError();
			}
			const offset = row * sequenceLength + column;
			inputIds[offset] = BigInt(tokenId);
			attentionMask[offset] = 1n;
		}
	}

	const dimensions = [request.texts.length, sequenceLength];
	const outputMap = await session.run({
		[INPUT_IDS]: new ort.Tensor("int64", inputIds, dimensions),
		[ATTENTION_MASK]: new ort.Tensor("int64", attentionMask, dimensions),
	});
	const output = outputMap[OUTPUT];
	if (
		!(output instanceof ort.Tensor) ||
		output.type !== "float32" ||
		!(output.data instanceof Float32Array) ||
		output.dims.length !== 3 ||
		output.dims[0] !== request.texts.length ||
		output.dims[1] !== sequenceLength ||
		output.dims[2] !== BGE_M3_DIMENSIONS
	) {
		throw new BgeM3ModelOutputError();
	}

	const vectors: ArrayBuffer[] = [];
	for (let row = 0; row < request.texts.length; row += 1) {
		const start = row * sequenceLength * BGE_M3_DIMENSIONS;
		const vector = normalizeDenseVector(output.data.subarray(start, start + BGE_M3_DIMENSIONS));
		vectors.push(vector.buffer as ArrayBuffer);
	}
	return vectors;
}

function truncate(ids: readonly number[], maxTokens: number): readonly number[] {
	if (ids.length < 2 || ids[0] !== CLS_TOKEN_ID || ids.at(-1) !== EOS_TOKEN_ID) {
		throw new BgeM3ModelOutputError();
	}
	if (ids.length <= maxTokens) return ids;
	return [...ids.slice(0, maxTokens - 1), EOS_TOKEN_ID];
}

function readJson(bytes: Buffer): unknown {
	try {
		return JSON.parse(bytes.toString("utf8"));
	} catch {
		throw new ArtifactFailure();
	}
}

function validateModelConfig(value: unknown): void {
	const config = asRecord(value);
	if (
		config.model_type !== "xlm-roberta" ||
		config.hidden_size !== BGE_M3_DIMENSIONS ||
		config.bos_token_id !== CLS_TOKEN_ID ||
		config.pad_token_id !== PAD_TOKEN_ID ||
		config.eos_token_id !== EOS_TOKEN_ID ||
		typeof config.max_position_embeddings !== "number" ||
		config.max_position_embeddings < BGE_M3_MAX_TOKENS + 2
	) {
		throw new ConfigurationFailure();
	}
}

function validateTokenizerConfig(value: unknown): void {
	const config = asRecord(value);
	if (
		config.tokenizer_class !== "XLMRobertaTokenizer" ||
		config.model_max_length !== BGE_M3_MAX_TOKENS ||
		config.bos_token !== "<s>" ||
		config.cls_token !== "<s>" ||
		config.pad_token !== "<pad>" ||
		config.eos_token !== "</s>"
	) {
		throw new ConfigurationFailure();
	}
}

function validateSession(session: ort.InferenceSession): void {
	const inputIds = session.inputMetadata.find((metadata) => metadata.name === INPUT_IDS);
	const attentionMask = session.inputMetadata.find((metadata) => metadata.name === ATTENTION_MASK);
	const output = session.outputMetadata.find((metadata) => metadata.name === OUTPUT);
	if (
		inputIds?.isTensor !== true ||
		inputIds.type !== "int64" ||
		attentionMask?.isTensor !== true ||
		attentionMask.type !== "int64" ||
		output?.isTensor !== true ||
		output.type !== "float32" ||
		output.shape.at(-1) !== BGE_M3_DIMENSIONS
	) {
		throw new ConfigurationFailure();
	}
}

function asRecord(value: unknown): Record<string, unknown> {
	if (typeof value !== "object" || value === null || Array.isArray(value)) throw new ConfigurationFailure();
	return value as Record<string, unknown>;
}

function asObject(value: unknown): object {
	return asRecord(value);
}

function post(message: BgeM3WorkerResponse, transferList: readonly ArrayBuffer[] = []): void {
	port?.postMessage(message, [...transferList]);
}

function classifyInitializationFailure(
	error: unknown,
): "invalid_artifact" | "invalid_configuration" | "worker_failure" {
	if (error instanceof ArtifactFailure || error instanceof PinnedArtifactError) return "invalid_artifact";
	if (error instanceof ConfigurationFailure) return "invalid_configuration";
	return "worker_failure";
}

class ArtifactFailure extends Error {}
class ConfigurationFailure extends Error {}
