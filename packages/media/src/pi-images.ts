import type { ImagesApi, ImagesModel, ImagesModels, Usage } from "@earendil-works/pi-ai";
import type { JsonValue } from "@opengameagent/protocol";
import type {
	GameMediaBinary,
	GameMediaGenerationRequest,
	GameMediaGenerationResult,
	GameMediaGenerator,
	GameMediaKind,
} from "./media.js";

function strictBase64(value: string, maximumBytes: number): Uint8Array {
	if (value.length === 0 || value.length % 4 !== 0 || !/^[A-Za-z0-9+/]*={0,2}$/u.test(value)) {
		throw new TypeError("The image provider returned malformed base64 data.");
	}
	const bytes = Buffer.from(value, "base64");
	if (bytes.byteLength < 1 || bytes.byteLength > maximumBytes) {
		throw new RangeError("The image provider output exceeds its byte limit.");
	}
	if (bytes.toString("base64") !== value) throw new TypeError("The image provider returned non-canonical base64 data.");
	return bytes;
}

function usageValue(usage: Usage | undefined): JsonValue | undefined {
	if (!usage) return undefined;
	return {
		input: usage.input,
		output: usage.output,
		cacheRead: usage.cacheRead,
		cacheWrite: usage.cacheWrite,
		...(usage.reasoning === undefined ? {} : { reasoning: usage.reasoning }),
		totalTokens: usage.totalTokens,
		cost: { ...usage.cost },
	};
}

export interface PiGameImageGeneratorOptions {
	models: ImagesModels;
	model: ImagesModel<ImagesApi>;
	maximumOutputBytes?: number;
	maximumOutputs?: number;
}

export class PiGameImageGenerator implements GameMediaGenerator {
	readonly provider: string;
	readonly model: string;
	readonly kinds: readonly GameMediaKind[] = ["image"];
	private readonly maximumOutputBytes: number;
	private readonly maximumOutputs: number;

	constructor(private readonly options: PiGameImageGeneratorOptions) {
		this.provider = options.model.provider;
		this.model = options.model.id;
		this.maximumOutputBytes = options.maximumOutputBytes ?? 64 * 1024 * 1024;
		this.maximumOutputs = options.maximumOutputs ?? 16;
		if (options.models.getModel(this.provider, this.model) !== options.model) {
			throw new Error("The Pi image model is not registered in the supplied model collection.");
		}
	}

	async generate(
		request: GameMediaGenerationRequest,
		_onProgress?: undefined,
		signal?: AbortSignal,
	): Promise<GameMediaGenerationResult> {
		if (request.kind !== "image") throw new TypeError("PiGameImageGenerator only supports image output.");
		if (request.sources.some((source) => source.kind !== "image")) {
			throw new TypeError("PiGameImageGenerator only accepts image reference sources.");
		}
		signal?.throwIfAborted();
		const response = await this.options.models.generateImages(
			this.options.model,
			{
				input: [
					{ type: "text", text: request.prompt },
					...request.sources.map((source) => ({
						type: "image" as const,
						data: Buffer.from(source.data).toString("base64"),
						mimeType: source.mimeType,
					})),
				],
			},
			{
				...(signal === undefined ? {} : { signal }),
				...(request.parameters === undefined ? {} : { metadata: request.parameters }),
			},
		);
		if (response.stopReason === "aborted") throw new DOMException("Image generation was aborted.", "AbortError");
		if (response.stopReason === "error") throw new Error("The image provider failed to generate an output.");
		const outputs: GameMediaBinary[] = [];
		for (const part of response.output) {
			if (part.type !== "image") continue;
			if (outputs.length >= this.maximumOutputs) throw new RangeError("The image provider returned too many outputs.");
			outputs.push({ kind: "image", mimeType: part.mimeType, data: strictBase64(part.data, this.maximumOutputBytes) });
		}
		if (outputs.length === 0) throw new Error("The image provider returned no image output.");
		const usage = usageValue(response.usage);
		return {
			outputs,
			provider: response.provider,
			model: response.model,
			...(response.responseId === undefined ? {} : { responseId: response.responseId }),
			...(usage === undefined ? {} : { usage }),
		};
	}
}
