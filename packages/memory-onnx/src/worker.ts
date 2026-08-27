import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { parentPort, workerData } from "node:worker_threads";
import { Tokenizer } from "@huggingface/tokenizers";
import { InferenceSession, Tensor } from "onnxruntime-node";

interface WorkerConfiguration {
	modelDirectory: string;
	dimensions: number;
}

interface EmbedRequest {
	type: "embed";
	id: number;
	texts: string[];
	maximumTokens: number;
}

interface LoadedModel {
	tokenizer: Tokenizer;
	session: InferenceSession;
	loadMilliseconds: number;
}

const configuration = workerData as WorkerConfiguration;
if (!parentPort) throw new Error("The BGE-M3 worker requires a parent port.");

let loadedPromise: Promise<LoadedModel> | undefined;
let loadReported = false;

async function loadJson(path: string): Promise<object> {
	const parsed: unknown = JSON.parse(await readFile(path, "utf8"));
	if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) throw new Error("load");
	return parsed;
}

async function load(): Promise<LoadedModel> {
	if (!loadedPromise) {
		loadedPromise = (async () => {
			const started = performance.now();
			const [tokenizerJson, tokenizerConfig, session] = await Promise.all([
				loadJson(resolve(configuration.modelDirectory, "tokenizer.json")),
				loadJson(resolve(configuration.modelDirectory, "tokenizer_config.json")),
				InferenceSession.create(resolve(configuration.modelDirectory, "onnx/model_int8.onnx"), {
					executionProviders: ["cpu"],
					graphOptimizationLevel: "all",
					logSeverityLevel: 4,
				}),
			]);
			return {
				tokenizer: new Tokenizer(tokenizerJson, tokenizerConfig),
				session,
				loadMilliseconds: performance.now() - started,
			};
		})();
	}
	return await loadedPromise;
}

function tensorData(output: InferenceSession.ReturnType, dimensions: number): Tensor {
	const candidate =
		output["last_hidden_state"] ??
		output["token_embeddings"] ??
		Object.values(output).find(
			(value) => value instanceof Tensor && value.type === "float32" && value.dims.length === 3,
		);
	if (!(candidate instanceof Tensor) || candidate.type !== "float32" || candidate.dims[2] !== dimensions) {
		throw new Error("invalid-output");
	}
	return candidate;
}

function maskedMeanPool(hidden: Tensor, mask: Tensor, dimensions: number): Float32Array[] {
	if (!(hidden.data instanceof Float32Array) || !(mask.data instanceof BigInt64Array)) {
		throw new Error("invalid-output");
	}
	const batch = hidden.dims[0];
	const tokens = hidden.dims[1];
	const width = hidden.dims[2];
	if (!batch || !tokens || width !== dimensions || mask.dims[0] !== batch || mask.dims[1] !== tokens) {
		throw new Error("invalid-output");
	}
	const vectors: Float32Array[] = [];
	for (let batchIndex = 0; batchIndex < batch; batchIndex += 1) {
		const vector = new Float32Array(dimensions);
		let included = 0;
		for (let token = 0; token < tokens; token += 1) {
			if (Number(mask.data[batchIndex * tokens + token] ?? 0n) === 0) continue;
			included += 1;
			const offset = (batchIndex * tokens + token) * dimensions;
			for (let dimension = 0; dimension < dimensions; dimension += 1) {
				vector[dimension] = (vector[dimension] ?? 0) + (hidden.data[offset + dimension] ?? 0);
			}
		}
		if (included < 1) throw new Error("invalid-output");
		let squared = 0;
		for (let dimension = 0; dimension < dimensions; dimension += 1) {
			const value = (vector[dimension] ?? 0) / included;
			vector[dimension] = value;
			squared += value * value;
		}
		if (!(squared > 0) || !Number.isFinite(squared)) throw new Error("invalid-output");
		const scale = 1 / Math.sqrt(squared);
		for (let dimension = 0; dimension < dimensions; dimension += 1) {
			vector[dimension] = (vector[dimension] ?? 0) * scale;
		}
		vectors.push(vector);
	}
	return vectors;
}

function tokenize(
	tokenizer: Tokenizer,
	texts: readonly string[],
	maximumTokens: number,
): {
	feeds: Record<string, Tensor>;
	mask: Tensor;
	truncatedInputs: number;
} {
	const encoded = texts.map((text) => tokenizer.encode(text));
	const truncatedInputs = encoded.filter((item) => item.ids.length > maximumTokens).length;
	const ids = encoded.map((item) => {
		if (item.ids.length <= maximumTokens) return item.ids;
		return [...item.ids.slice(0, maximumTokens - 1), item.ids.at(-1) ?? 2];
	});
	const width = Math.max(...ids.map((item) => item.length));
	const inputIds = new BigInt64Array(texts.length * width).fill(1n);
	const attentionMask = new BigInt64Array(texts.length * width);
	for (let batch = 0; batch < ids.length; batch += 1) {
		const tokens = ids[batch] ?? [];
		for (let token = 0; token < tokens.length; token += 1) {
			inputIds[batch * width + token] = BigInt(tokens[token] ?? 1);
			attentionMask[batch * width + token] = 1n;
		}
	}
	const shape = [texts.length, width];
	const mask = new Tensor("int64", attentionMask, shape);
	return {
		feeds: {
			input_ids: new Tensor("int64", inputIds, shape),
			attention_mask: mask,
		},
		mask,
		truncatedInputs,
	};
}

function classify(error: unknown, phase: "load" | "tokenization" | "inference"): string {
	if (error instanceof Error && error.message === "invalid-output") return "invalid-output";
	return phase;
}

parentPort.on("message", async (request: EmbedRequest) => {
	if (request.type !== "embed") return;
	let phase: "load" | "tokenization" | "inference" = "load";
	try {
		const loaded = await load();
		phase = "tokenization";
		const tokenizationStarted = performance.now();
		const { feeds, mask, truncatedInputs } = tokenize(loaded.tokenizer, request.texts, request.maximumTokens);
		const tokenizationMilliseconds = performance.now() - tokenizationStarted;
		phase = "inference";
		const inferenceStarted = performance.now();
		const outputs = await loaded.session.run(feeds);
		const vectors = maskedMeanPool(tensorData(outputs, configuration.dimensions), mask, configuration.dimensions);
		const inferenceMilliseconds = performance.now() - inferenceStarted;
		const buffers = vectors.map((vector) => vector.buffer as ArrayBuffer);
		parentPort?.postMessage(
			{
				id: request.id,
				ok: true,
				vectors: buffers,
				loadMilliseconds: loadReported ? 0 : loaded.loadMilliseconds,
				tokenizationMilliseconds,
				inferenceMilliseconds,
				truncatedInputs,
			},
			buffers,
		);
		loadReported = true;
	} catch (error) {
		parentPort?.postMessage({ id: request.id, ok: false, failure: classify(error, phase) });
	}
});
