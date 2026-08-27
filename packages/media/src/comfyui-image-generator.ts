import { createHash, randomUUID } from "node:crypto";
import { setTimeout as delay } from "node:timers/promises";
import type { JsonObject } from "@opengameagent/protocol";
import type {
	GameMediaBinary,
	GameMediaGenerationProgress,
	GameMediaGenerationRequest,
	GameMediaGenerationResult,
	GameMediaGenerator,
	GameMediaProviderAuthentication,
} from "./media.js";
import { validateMediaIdentifier } from "./media.js";

export interface ComfyUiWorkflowInputTarget {
	nodeId: string;
	input: string;
}

export interface ComfyUiImageGeneratorOptions {
	provider?: string;
	model: string;
	endpoint?: string;
	workflow: JsonObject;
	promptTarget: ComfyUiWorkflowInputTarget;
	referenceTargets?: readonly ComfyUiWorkflowInputTarget[];
	outputNodeIds?: readonly string[];
	authentication?: GameMediaProviderAuthentication;
	fetch?: typeof globalThis.fetch;
	pollIntervalMilliseconds?: number;
	maximumReferences?: number;
	maximumReferenceBytes?: number;
	maximumResponseBytes?: number;
	maximumOutputBytes?: number;
	maximumOutputs?: number;
}

interface CheckedOptions {
	provider: string;
	model: string;
	endpoint: URL;
	workflow: JsonObject;
	promptTarget: ComfyUiWorkflowInputTarget;
	referenceTargets: readonly ComfyUiWorkflowInputTarget[];
	outputNodeIds: readonly string[];
	authentication?: GameMediaProviderAuthentication;
	fetch: typeof globalThis.fetch;
	pollIntervalMilliseconds: number;
	maximumReferences: number;
	maximumReferenceBytes: number;
	maximumResponseBytes: number;
	maximumOutputBytes: number;
	maximumOutputs: number;
}

interface ComfyOutputReference {
	filename: string;
	subfolder: string;
	type: string;
}

function positive(value: number | undefined, fallback: number, name: string, maximum: number): number {
	const resolved = value ?? fallback;
	if (!Number.isInteger(resolved) || resolved < 1 || resolved > maximum) throw new RangeError(`${name} is invalid.`);
	return resolved;
}

function checkedEndpoint(value: string): URL {
	const endpoint = new URL(value);
	const loopback =
		endpoint.hostname === "localhost" || endpoint.hostname === "127.0.0.1" || endpoint.hostname === "[::1]";
	if (!loopback || (endpoint.protocol !== "http:" && endpoint.protocol !== "https:")) {
		throw new TypeError("ComfyUI must use a loopback HTTP or HTTPS endpoint.");
	}
	if (endpoint.username || endpoint.password || endpoint.hash || endpoint.search) {
		throw new TypeError("The ComfyUI endpoint is invalid.");
	}
	if (!endpoint.pathname.endsWith("/")) endpoint.pathname += "/";
	return endpoint;
}

function checkedTarget(target: ComfyUiWorkflowInputTarget): ComfyUiWorkflowInputTarget {
	return {
		nodeId: validateMediaIdentifier(target.nodeId, "ComfyUI node id"),
		input: validateMediaIdentifier(target.input, "ComfyUI input name"),
	};
}

function checkedOptions(options: ComfyUiImageGeneratorOptions): CheckedOptions {
	return {
		provider: validateMediaIdentifier(options.provider ?? "comfyui", "Provider id"),
		model: validateMediaIdentifier(options.model, "Model id"),
		endpoint: checkedEndpoint(options.endpoint ?? "http://127.0.0.1:8188/"),
		workflow: structuredClone(options.workflow),
		promptTarget: checkedTarget(options.promptTarget),
		referenceTargets: (options.referenceTargets ?? []).map(checkedTarget),
		outputNodeIds: (options.outputNodeIds ?? []).map((value) => validateMediaIdentifier(value, "Output node id")),
		...(options.authentication === undefined ? {} : { authentication: options.authentication }),
		fetch: options.fetch ?? globalThis.fetch,
		pollIntervalMilliseconds: positive(options.pollIntervalMilliseconds, 250, "pollIntervalMilliseconds", 60_000),
		maximumReferences: positive(options.maximumReferences, 8, "maximumReferences", 128),
		maximumReferenceBytes: positive(options.maximumReferenceBytes, 20_000_000, "maximumReferenceBytes", 512_000_000),
		maximumResponseBytes: positive(options.maximumResponseBytes, 16_000_000, "maximumResponseBytes", 256_000_000),
		maximumOutputBytes: positive(options.maximumOutputBytes, 64_000_000, "maximumOutputBytes", 1_000_000_000),
		maximumOutputs: positive(options.maximumOutputs, 8, "maximumOutputs", 128),
	};
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

function workflowInputs(workflow: JsonObject, target: ComfyUiWorkflowInputTarget): JsonObject {
	const node = workflow[target.nodeId];
	if (!isRecord(node) || !isRecord(node["inputs"])) {
		throw new TypeError("The configured ComfyUI workflow target does not exist.");
	}
	return node["inputs"] as JsonObject;
}

function path(options: CheckedOptions, relative: string): URL {
	return new URL(relative, options.endpoint);
}

async function headers(options: CheckedOptions, signal?: AbortSignal): Promise<Headers> {
	const result = new Headers();
	const authentication = await options.authentication?.resolve(options.provider, signal);
	for (const [name, value] of Object.entries(authentication?.headers ?? {})) {
		if (!/^[a-z0-9-]{1,64}$/iu.test(name) || /[\r\n\0]/u.test(value) || value.length > 8_192) {
			throw new TypeError("ComfyUI authentication returned an invalid header.");
		}
		result.set(name, value);
	}
	if (authentication?.bearerToken) result.set("authorization", `Bearer ${authentication.bearerToken}`);
	return result;
}

async function boundedBytes(response: Response, maximum: number): Promise<Uint8Array> {
	const declared = Number(response.headers.get("content-length"));
	if (Number.isFinite(declared) && declared > maximum)
		throw new RangeError("The ComfyUI response exceeds its byte limit.");
	if (!response.body) return new Uint8Array();
	const reader = response.body.getReader();
	const chunks: Uint8Array[] = [];
	let total = 0;
	for (;;) {
		const { done, value } = await reader.read();
		if (done) break;
		total += value.byteLength;
		if (total > maximum) {
			await reader.cancel();
			throw new RangeError("The ComfyUI response exceeds its byte limit.");
		}
		chunks.push(value);
	}
	const result = new Uint8Array(total);
	let offset = 0;
	for (const chunk of chunks) {
		result.set(chunk, offset);
		offset += chunk.byteLength;
	}
	return result;
}

async function boundedJson(response: Response, maximum: number): Promise<unknown> {
	const bytes = await boundedBytes(response, maximum);
	try {
		return JSON.parse(Buffer.from(bytes).toString("utf8"));
	} catch {
		throw new Error("ComfyUI returned invalid JSON.");
	}
}

function imageExtension(mimeType: string): string {
	switch (mimeType.toLowerCase()) {
		case "image/jpeg":
			return "jpg";
		case "image/webp":
			return "webp";
		case "image/gif":
			return "gif";
		case "image/png":
			return "png";
		default:
			throw new TypeError("ComfyUI references must use PNG, JPEG, WebP, or GIF.");
	}
}

function outputReferences(
	value: unknown,
	promptId: string,
	allowedNodes: ReadonlySet<string>,
): ComfyOutputReference[] | undefined {
	if (!isRecord(value)) return undefined;
	const item = value[promptId];
	if (!isRecord(item)) return undefined;
	if (isRecord(item["status"]) && item["status"]["completed"] === false) {
		const messages = item["status"]["messages"];
		if (Array.isArray(messages) && messages.some((entry) => Array.isArray(entry) && entry[0] === "execution_error")) {
			throw new Error("ComfyUI workflow execution failed.");
		}
		return undefined;
	}
	if (!isRecord(item["outputs"])) return undefined;
	const references: ComfyOutputReference[] = [];
	for (const [nodeId, output] of Object.entries(item["outputs"])) {
		if (allowedNodes.size > 0 && !allowedNodes.has(nodeId)) continue;
		if (!isRecord(output) || !Array.isArray(output["images"])) continue;
		for (const image of output["images"]) {
			if (!isRecord(image)) continue;
			const { filename, subfolder, type } = image;
			if (typeof filename !== "string" || typeof subfolder !== "string" || typeof type !== "string") continue;
			if ([filename, subfolder, type].some((part) => part.length > 1_024 || /[\r\n\0]/u.test(part))) continue;
			references.push({ filename, subfolder, type });
		}
	}
	return references.length > 0 ? references : undefined;
}

function outputMime(response: Response): string {
	const mimeType = response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase();
	if (!mimeType?.startsWith("image/") || mimeType.length > 127)
		throw new TypeError("ComfyUI returned a non-image output.");
	return mimeType;
}

export class ComfyUiImageGenerator implements GameMediaGenerator {
	readonly provider: string;
	readonly model: string;
	readonly kinds = ["image"] as const;
	private readonly options: CheckedOptions;

	constructor(options: ComfyUiImageGeneratorOptions) {
		this.options = checkedOptions(options);
		this.provider = this.options.provider;
		this.model = this.options.model;
		workflowInputs(this.options.workflow, this.options.promptTarget);
		for (const target of this.options.referenceTargets) workflowInputs(this.options.workflow, target);
	}

	async generate(
		request: GameMediaGenerationRequest,
		onProgress?: (progress: GameMediaGenerationProgress) => void | Promise<void>,
		signal?: AbortSignal,
	): Promise<GameMediaGenerationResult> {
		if (request.kind !== "image") throw new TypeError("ComfyUI only supports image generation.");
		if (
			request.sources.length > this.options.maximumReferences ||
			request.sources.length > this.options.referenceTargets.length
		) {
			throw new RangeError("The ComfyUI reference count exceeds the configured workflow inputs.");
		}
		const workflow = structuredClone(this.options.workflow);
		workflowInputs(workflow, this.options.promptTarget)[this.options.promptTarget.input] = request.prompt;
		for (const [index, source] of request.sources.entries()) {
			if (
				source.kind !== "image" ||
				source.data.byteLength < 1 ||
				source.data.byteLength > this.options.maximumReferenceBytes
			) {
				throw new RangeError("A ComfyUI image reference is invalid or exceeds its byte limit.");
			}
			const name = `${createHash("sha256").update(source.data).digest("hex")}.${imageExtension(source.mimeType)}`;
			await this.upload(source, name, signal);
			const target = this.options.referenceTargets[index];
			if (!target) throw new Error("The ComfyUI reference target is missing.");
			workflowInputs(workflow, target)[target.input] = name;
		}
		await onProgress?.({ stage: "submitted", fraction: 0 });
		const promptId = await this.submit(workflow, request.id, signal);
		try {
			const references = await this.waitForOutputs(promptId, onProgress, signal);
			const outputs: GameMediaBinary[] = [];
			for (const reference of references) outputs.push(await this.download(reference, signal));
			await onProgress?.({ stage: "completed", fraction: 1 });
			return { provider: this.provider, model: this.model, responseId: promptId, outputs };
		} catch (error) {
			if (signal?.aborted) await this.cancelQueued(promptId);
			throw error;
		}
	}

	private async upload(source: GameMediaBinary, name: string, signal?: AbortSignal): Promise<void> {
		const form = new FormData();
		form.set("image", new Blob([source.data], { type: source.mimeType }), name);
		form.set("type", "input");
		form.set("overwrite", "false");
		const response = await this.options.fetch(path(this.options, "upload/image"), {
			method: "POST",
			headers: await headers(this.options, signal),
			body: form,
			...(signal === undefined ? {} : { signal }),
			redirect: "error",
		});
		if (!response.ok) throw new Error(`ComfyUI reference upload returned HTTP ${response.status}.`);
		await boundedBytes(response, this.options.maximumResponseBytes);
	}

	private async submit(workflow: JsonObject, requestId: string, signal?: AbortSignal): Promise<string> {
		const requestHeaders = await headers(this.options, signal);
		requestHeaders.set("content-type", "application/json");
		const clientId = createHash("sha256").update(`${requestId}\0${randomUUID()}`).digest("hex").slice(0, 32);
		const response = await this.options.fetch(path(this.options, "prompt"), {
			method: "POST",
			headers: requestHeaders,
			body: JSON.stringify({ prompt: workflow, client_id: clientId }),
			...(signal === undefined ? {} : { signal }),
			redirect: "error",
		});
		if (!response.ok) throw new Error(`ComfyUI prompt submission returned HTTP ${response.status}.`);
		const body = await boundedJson(response, this.options.maximumResponseBytes);
		const promptId = isRecord(body) ? body["prompt_id"] : undefined;
		if (typeof promptId !== "string" || !/^[a-z0-9_-]{1,192}$/iu.test(promptId)) {
			throw new Error("ComfyUI returned an invalid prompt id.");
		}
		return promptId;
	}

	private async waitForOutputs(
		promptId: string,
		onProgress?: (progress: GameMediaGenerationProgress) => void | Promise<void>,
		signal?: AbortSignal,
	): Promise<ComfyOutputReference[]> {
		const allowedNodes = new Set(this.options.outputNodeIds);
		for (;;) {
			signal?.throwIfAborted();
			const response = await this.options.fetch(path(this.options, `history/${encodeURIComponent(promptId)}`), {
				headers: await headers(this.options, signal),
				...(signal === undefined ? {} : { signal }),
				redirect: "error",
			});
			if (!response.ok) throw new Error(`ComfyUI history returned HTTP ${response.status}.`);
			const references = outputReferences(
				await boundedJson(response, this.options.maximumResponseBytes),
				promptId,
				allowedNodes,
			);
			if (references) {
				if (references.length > this.options.maximumOutputs) throw new RangeError("ComfyUI returned too many images.");
				return references;
			}
			await onProgress?.({ stage: "running" });
			await delay(this.options.pollIntervalMilliseconds, undefined, { signal });
		}
	}

	private async download(reference: ComfyOutputReference, signal?: AbortSignal): Promise<GameMediaBinary> {
		const url = path(this.options, "view");
		url.searchParams.set("filename", reference.filename);
		url.searchParams.set("subfolder", reference.subfolder);
		url.searchParams.set("type", reference.type);
		const response = await this.options.fetch(url, {
			headers: await headers(this.options, signal),
			...(signal === undefined ? {} : { signal }),
			redirect: "error",
		});
		if (!response.ok) throw new Error(`ComfyUI output download returned HTTP ${response.status}.`);
		const mimeType = outputMime(response);
		const data = await boundedBytes(response, this.options.maximumOutputBytes);
		if (data.byteLength < 1) throw new Error("ComfyUI returned an empty image.");
		return { kind: "image", mimeType, data, name: reference.filename };
	}

	private async cancelQueued(promptId: string): Promise<void> {
		try {
			const requestHeaders = await headers(this.options);
			requestHeaders.set("content-type", "application/json");
			await this.options.fetch(path(this.options, "queue"), {
				method: "POST",
				headers: requestHeaders,
				body: JSON.stringify({ delete: [promptId] }),
				redirect: "error",
			});
		} catch {
			// Cancellation is best effort. Never mask the caller's abort reason.
		}
	}
}
