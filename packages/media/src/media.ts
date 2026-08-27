import type { GameSessionKey, JsonObject, JsonValue } from "@opengameagent/protocol";

export type GameMediaKind = "image" | "audio" | "video";

export interface GameMediaResource {
	id: string;
	sha256: string;
	kind: GameMediaKind;
	mimeType: string;
	bytes: number;
	name?: string;
}

export interface GameMediaBinary {
	kind: GameMediaKind;
	mimeType: string;
	data: Uint8Array;
	name?: string;
}

export interface GameMediaGenerationRequest {
	id: string;
	session: GameSessionKey;
	kind: GameMediaKind;
	prompt: string;
	sources: readonly GameMediaBinary[];
	parameters?: JsonObject;
}

export interface GameMediaGenerationProgress {
	stage: string;
	fraction?: number;
	metadata?: JsonObject;
}

export interface GameMediaGenerationResult {
	outputs: readonly GameMediaBinary[];
	provider: string;
	model: string;
	responseId?: string;
	usage?: JsonValue;
}

export interface GameMediaGenerator {
	readonly provider: string;
	readonly model: string;
	readonly kinds: readonly GameMediaKind[];
	generate(
		request: GameMediaGenerationRequest,
		onProgress?: (progress: GameMediaGenerationProgress) => void | Promise<void>,
		signal?: AbortSignal,
	): Promise<GameMediaGenerationResult>;
}

export interface GameMediaProviderAuthenticationResult {
	bearerToken?: string;
	headers?: Readonly<Record<string, string>>;
}

export interface GameMediaProviderAuthentication {
	resolve(provider: string, signal?: AbortSignal): Promise<GameMediaProviderAuthenticationResult | undefined>;
}

export interface GameMediaResourceStore {
	save(binary: GameMediaBinary, signal?: AbortSignal): Promise<GameMediaResource>;
	read(resource: GameMediaResource, signal?: AbortSignal): Promise<GameMediaBinary>;
}

export interface GameMediaRegistryOptions {
	maximumGenerators?: number;
	maximumSources?: number;
	maximumOutputs?: number;
	maximumPromptBytes?: number;
	maximumSourceBytes?: number;
	maximumAggregateSourceBytes?: number;
	maximumOutputBytes?: number;
	maximumAggregateOutputBytes?: number;
	timeoutMilliseconds?: number;
}

const identifierPattern = /^[a-z0-9][a-z0-9._:/-]{0,191}$/i;

export function validateMediaIdentifier(value: string, name: string): string {
	if (!identifierPattern.test(value)) throw new TypeError(`${name} must be a bounded portable identifier.`);
	return value;
}

export function mediaByteLength(value: string): number {
	return Buffer.byteLength(value, "utf8");
}

function positiveInteger(value: number, name: string, maximum: number): number {
	if (!Number.isInteger(value) || value < 1 || value > maximum) {
		throw new RangeError(`${name} must be a positive bounded integer.`);
	}
	return value;
}

function boundBinary(binary: GameMediaBinary, maximum: number, label: string): void {
	if (!(binary.data instanceof Uint8Array) || binary.data.byteLength < 1 || binary.data.byteLength > maximum) {
		throw new RangeError(`${label} exceeds its byte limit.`);
	}
	if (!/^[a-z0-9.+-]+\/[a-z0-9.+-]+$/i.test(binary.mimeType) || binary.mimeType.length > 127) {
		throw new TypeError(`${label} has an invalid MIME type.`);
	}
	if (
		binary.name !== undefined &&
		(binary.name.length > 255 ||
			[...binary.name].some((character) => (character.codePointAt(0) ?? 0) < 32 || character.codePointAt(0) === 127))
	) {
		throw new TypeError(`${label} has an invalid name.`);
	}
}

export class GameMediaRegistry {
	private readonly generators = new Map<string, GameMediaGenerator>();
	private readonly options: Required<GameMediaRegistryOptions>;

	constructor(options: GameMediaRegistryOptions = {}) {
		this.options = {
			maximumGenerators: positiveInteger(options.maximumGenerators ?? 128, "maximumGenerators", 10_000),
			maximumSources: positiveInteger(options.maximumSources ?? 32, "maximumSources", 1_024),
			maximumOutputs: positiveInteger(options.maximumOutputs ?? 16, "maximumOutputs", 1_024),
			maximumPromptBytes: positiveInteger(options.maximumPromptBytes ?? 1_000_000, "maximumPromptBytes", 16_000_000),
			maximumSourceBytes: positiveInteger(
				options.maximumSourceBytes ?? 32 * 1024 * 1024,
				"maximumSourceBytes",
				512 * 1024 * 1024,
			),
			maximumAggregateSourceBytes: positiveInteger(
				options.maximumAggregateSourceBytes ?? 128 * 1024 * 1024,
				"maximumAggregateSourceBytes",
				1024 * 1024 * 1024,
			),
			maximumOutputBytes: positiveInteger(
				options.maximumOutputBytes ?? 64 * 1024 * 1024,
				"maximumOutputBytes",
				1024 * 1024 * 1024,
			),
			maximumAggregateOutputBytes: positiveInteger(
				options.maximumAggregateOutputBytes ?? 256 * 1024 * 1024,
				"maximumAggregateOutputBytes",
				2_000_000_000,
			),
			timeoutMilliseconds: positiveInteger(
				options.timeoutMilliseconds ?? 10 * 60_000,
				"timeoutMilliseconds",
				60 * 60_000,
			),
		};
	}

	register(generator: GameMediaGenerator): void {
		const key = this.key(generator.provider, generator.model);
		if (this.generators.has(key)) throw new Error(`Media generator '${key}' is already registered.`);
		if (this.generators.size >= this.options.maximumGenerators) throw new Error("Media generator capacity reached.");
		if (generator.kinds.length < 1 || new Set(generator.kinds).size !== generator.kinds.length) {
			throw new TypeError("Media generator kinds must be non-empty and unique.");
		}
		this.generators.set(key, generator);
	}

	list(): readonly GameMediaGenerator[] {
		return [...this.generators.values()];
	}

	async generate(
		provider: string,
		model: string,
		request: GameMediaGenerationRequest,
		onProgress?: (progress: GameMediaGenerationProgress) => void | Promise<void>,
		signal?: AbortSignal,
	): Promise<GameMediaGenerationResult> {
		const generator = this.generators.get(this.key(provider, model));
		if (!generator) throw new Error("The requested media generator is not registered.");
		if (!generator.kinds.includes(request.kind))
			throw new Error("The media generator does not support this output kind.");
		this.validateRequest(request);
		signal?.throwIfAborted();
		const timeout = AbortSignal.timeout(this.options.timeoutMilliseconds);
		const combined = signal ? AbortSignal.any([signal, timeout]) : timeout;
		const result = await generator.generate(request, onProgress, combined);
		combined.throwIfAborted();
		if (result.provider !== generator.provider || result.model !== generator.model) {
			throw new Error("The media generator returned a mismatched provider or model identity.");
		}
		if (result.outputs.length < 1 || result.outputs.length > this.options.maximumOutputs) {
			throw new RangeError("The media generator returned an invalid output count.");
		}
		let bytes = 0;
		for (const output of result.outputs) {
			if (output.kind !== request.kind) throw new TypeError("The media generator returned a mismatched output kind.");
			boundBinary(output, this.options.maximumOutputBytes, "Media output");
			bytes += output.data.byteLength;
			if (bytes > this.options.maximumAggregateOutputBytes) {
				throw new RangeError("Media outputs exceed their aggregate byte limit.");
			}
		}
		return result;
	}

	private key(provider: string, model: string): string {
		return `${validateMediaIdentifier(provider, "Provider id")}\n${validateMediaIdentifier(model, "Model id")}`;
	}

	private validateRequest(request: GameMediaGenerationRequest): void {
		validateMediaIdentifier(request.id, "Media request id");
		if (mediaByteLength(request.prompt) > this.options.maximumPromptBytes || /\0/u.test(request.prompt)) {
			throw new RangeError("Media prompt exceeds its limit or contains invalid characters.");
		}
		if (request.sources.length > this.options.maximumSources)
			throw new RangeError("Media source count exceeds its limit.");
		let bytes = 0;
		for (const source of request.sources) {
			boundBinary(source, this.options.maximumSourceBytes, "Media source");
			bytes += source.data.byteLength;
			if (bytes > this.options.maximumAggregateSourceBytes) {
				throw new RangeError("Media sources exceed their aggregate byte limit.");
			}
		}
		if (!request.session.sessionId || !request.session.actorId)
			throw new TypeError("Media request session is invalid.");
	}
}
