import type { StreamFn } from "@earendil-works/pi-agent-core";
import {
	type Api,
	type Credential,
	type CredentialInfo,
	type CredentialStore,
	createModels,
	createProvider,
	type Model,
	type Models,
} from "@earendil-works/pi-ai";
import { openAICompletionsApi } from "@earendil-works/pi-ai/api/openai-completions.lazy";
import { openAIResponsesApi } from "@earendil-works/pi-ai/api/openai-responses.lazy";
import { builtinProviders } from "@earendil-works/pi-ai/providers/all";
import type { GameResolvedModel } from "@opengameagent/protocol";

export type GameThinkingLevel = "off" | "minimal" | "low" | "medium" | "high" | "xhigh" | "max";

export interface GameProviderCredentialSource {
	read(
		providerId: string,
		signal?: AbortSignal,
	): Promise<{ key?: string; environment?: Record<string, string> } | undefined>;
}

export interface PiGameModelProfile {
	id: string;
	provider: string;
	model: string;
	thinkingLevel?: GameThinkingLevel;
}

export interface PiOpenAICompatibleModel {
	id: string;
	name?: string;
	reasoning?: boolean;
	input?: readonly ("text" | "image")[];
	contextWindow: number;
	maximumOutputTokens: number;
	cost?: { input: number; output: number; cacheRead: number; cacheWrite: number };
	compatibility?: Model<"openai-completions">["compat"];
}

export interface PiOpenAICompatibleProvider {
	id: string;
	name?: string;
	baseUrl: string;
	protocol?: "completions" | "responses";
	requiresCredential?: boolean;
	/** Non-secret placeholder required by some OpenAI-compatible clients for an unauthenticated loopback server. */
	anonymousApiKey?: string;
	models: readonly PiOpenAICompatibleModel[];
}

export interface PiGameModelRegistryOptions {
	profiles: readonly PiGameModelProfile[];
	credentials?: GameProviderCredentialSource;
	providers?: readonly PiOpenAICompatibleProvider[];
	includeBuiltinProviders?: boolean;
}

export interface ResolvedPiGameModel {
	model: Model<Api>;
	streamFn: StreamFn;
	thinkingLevel: GameThinkingLevel;
	descriptor: GameResolvedModel;
}

export interface PiGameModelResolver {
	resolve(profileId: string): ResolvedPiGameModel;
}

class ReadOnlyPiCredentialStore implements CredentialStore {
	constructor(private readonly source?: GameProviderCredentialSource) {}

	async read(providerId: string, options?: { signal?: AbortSignal }): Promise<Credential | undefined> {
		options?.signal?.throwIfAborted();
		const credential = await this.source?.read(providerId, options?.signal);
		options?.signal?.throwIfAborted();
		if (!credential) return undefined;
		return {
			type: "api_key",
			...(credential.key === undefined ? {} : { key: credential.key }),
			...(credential.environment === undefined ? {} : { env: credential.environment }),
		};
	}

	async list(): Promise<readonly CredentialInfo[]> {
		return [];
	}

	async modify(
		_providerId: string,
		_fn: (current: Credential | undefined) => Promise<Credential | undefined>,
	): Promise<Credential | undefined> {
		throw new Error("The host credential boundary is read-only.");
	}

	async delete(): Promise<void> {
		throw new Error("The host credential boundary is read-only.");
	}
}

function validateIdentifier(value: string, name: string): void {
	if (!/^[a-z0-9][a-z0-9._-]{0,127}$/i.test(value)) {
		throw new TypeError(`${name} must be a bounded portable identifier.`);
	}
}

function validateEndpoint(value: string): string {
	const url = new URL(value);
	const loopback = url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]";
	if (url.protocol !== "https:" && !(url.protocol === "http:" && loopback)) {
		throw new TypeError("Model endpoints must use HTTPS, except for loopback HTTP services.");
	}
	if (url.username || url.password || url.hash)
		throw new TypeError("Model endpoints cannot contain credentials or fragments.");
	return url.toString().replace(/\/$/, "");
}

function customModel(provider: PiOpenAICompatibleProvider, model: PiOpenAICompatibleModel): Model<Api> {
	validateIdentifier(model.id, "Model id");
	if (!Number.isInteger(model.contextWindow) || model.contextWindow < 1024) {
		throw new RangeError("Model contextWindow must be an integer of at least 1024.");
	}
	if (!Number.isInteger(model.maximumOutputTokens) || model.maximumOutputTokens < 1) {
		throw new RangeError("Model maximumOutputTokens must be positive.");
	}
	return {
		id: model.id,
		name: model.name ?? model.id,
		api: provider.protocol === "responses" ? "openai-responses" : "openai-completions",
		provider: provider.id,
		baseUrl: validateEndpoint(provider.baseUrl),
		reasoning: model.reasoning ?? false,
		input: [...(model.input ?? ["text"])],
		cost: model.cost ?? { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
		contextWindow: model.contextWindow,
		maxTokens: model.maximumOutputTokens,
		...(provider.protocol === "responses" || model.compatibility === undefined ? {} : { compat: model.compatibility }),
	} as Model<Api>;
}

export class PiGameModelRegistry implements PiGameModelResolver {
	private readonly profiles = new Map<string, PiGameModelProfile>();

	constructor(
		private readonly models: Models,
		profiles: readonly PiGameModelProfile[],
	) {
		for (const profile of profiles) {
			validateIdentifier(profile.id, "Profile id");
			if (this.profiles.has(profile.id)) throw new Error(`Duplicate model profile '${profile.id}'.`);
			if (!models.getModel(profile.provider, profile.model)) {
				throw new Error(`Model profile '${profile.id}' resolves to an unknown provider/model.`);
			}
			this.profiles.set(profile.id, { ...profile });
		}
		if (this.profiles.size === 0) throw new Error("At least one model profile is required.");
	}

	resolve(profileId: string): ResolvedPiGameModel {
		const profile = this.profiles.get(profileId);
		if (!profile) throw new Error(`Unknown model profile '${profileId}'.`);
		const model = this.models.getModel(profile.provider, profile.model);
		if (!model) throw new Error(`Model profile '${profileId}' is no longer available.`);
		return {
			model,
			streamFn: this.models.streamSimple.bind(this.models),
			thinkingLevel: profile.thinkingLevel ?? "off",
			descriptor: {
				profileId,
				provider: model.provider,
				model: model.id,
				api: model.api,
				reasoning: model.reasoning,
				input: [...model.input],
				contextWindow: model.contextWindow,
				maximumOutputTokens: model.maxTokens,
			},
		};
	}
}

export function createPiGameModelRegistry(options: PiGameModelRegistryOptions): PiGameModelRegistry {
	const models = createModels({
		credentials: new ReadOnlyPiCredentialStore(options.credentials),
		authContext: {
			env: async () => undefined,
			fileExists: async () => false,
		},
	});
	if (options.includeBuiltinProviders !== false) {
		for (const provider of builtinProviders()) models.setProvider(provider);
	}
	for (const provider of options.providers ?? []) {
		validateIdentifier(provider.id, "Provider id");
		const api = provider.protocol === "responses" ? openAIResponsesApi() : openAICompletionsApi();
		models.setProvider(
			createProvider({
				id: provider.id,
				name: provider.name ?? provider.id,
				baseUrl: validateEndpoint(provider.baseUrl),
				auth: {
					apiKey: {
						name: `${provider.name ?? provider.id} credential`,
						resolve: async ({ credential }) => {
							if (credential?.key) {
								return {
									auth: { apiKey: credential.key },
									...(credential.env === undefined ? {} : { env: credential.env }),
								};
							}
							if (provider.requiresCredential === true) return undefined;
							return { auth: provider.anonymousApiKey === undefined ? {} : { apiKey: provider.anonymousApiKey } };
						},
					},
				},
				models: provider.models.map((model) => customModel(provider, model)),
				api,
			}),
		);
	}
	return new PiGameModelRegistry(models, options.profiles);
}
