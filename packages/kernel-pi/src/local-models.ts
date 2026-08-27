import type {
	GameThinkingLevel,
	PiGameModelProfile,
	PiOpenAICompatibleModel,
	PiOpenAICompatibleProvider,
} from "./model-registry.js";

export type LocalGameModelBackend = "ollama" | "lm-studio" | "localai" | "llama.cpp" | "vllm";

export interface LocalGameModelPresetOptions {
	backend: LocalGameModelBackend;
	model: string;
	profileId?: string;
	providerId?: string;
	endpoint?: string;
	protocol?: "completions" | "responses";
	contextWindow?: number;
	maximumOutputTokens?: number;
	input?: readonly ("text" | "image")[];
	reasoning?: boolean;
	thinkingLevel?: GameThinkingLevel;
	requiresCredential?: boolean;
	compatibility?: PiOpenAICompatibleModel["compatibility"];
}

export interface LocalGameModelPreset {
	provider: PiOpenAICompatibleProvider;
	profile: PiGameModelProfile;
}

const defaults: Record<
	LocalGameModelBackend,
	{ endpoint: string; anonymousApiKey: string; protocol: "completions" | "responses" }
> = {
	ollama: { endpoint: "http://127.0.0.1:11434/v1", anonymousApiKey: "ollama", protocol: "responses" },
	"lm-studio": { endpoint: "http://127.0.0.1:1234/v1", anonymousApiKey: "lm-studio", protocol: "responses" },
	localai: { endpoint: "http://127.0.0.1:8080/v1", anonymousApiKey: "local", protocol: "completions" },
	"llama.cpp": { endpoint: "http://127.0.0.1:8080/v1", anonymousApiKey: "local", protocol: "completions" },
	vllm: { endpoint: "http://127.0.0.1:8000/v1", anonymousApiKey: "local", protocol: "completions" },
};

function identifier(value: string, name: string): string {
	if (!/^[a-z0-9][a-z0-9._-]{0,127}$/iu.test(value)) throw new TypeError(`${name} is invalid.`);
	return value;
}

function localEndpoint(value: string): string {
	const url = new URL(value);
	const loopback = url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]";
	if (!loopback || (url.protocol !== "http:" && url.protocol !== "https:")) {
		throw new TypeError("Local model presets require a loopback endpoint.");
	}
	if (url.username || url.password || url.hash || url.search)
		throw new TypeError("The local model endpoint is invalid.");
	return url.toString().replace(/\/$/u, "");
}

/**
 * Creates a conservative, host-owned preset for a locally served model. The
 * caller may override model limits because OpenAI-compatible discovery does not
 * expose reliable context-window metadata across all supported backends.
 */
export function createLocalGameModelPreset(options: LocalGameModelPresetOptions): LocalGameModelPreset {
	const backend = defaults[options.backend];
	const providerId = identifier(options.providerId ?? options.backend, "Provider id");
	const profileId = identifier(
		options.profileId ?? `${providerId}-${options.model.replace(/[^a-z0-9._-]+/giu, "-")}`,
		"Profile id",
	);
	const modelId = identifier(options.model, "Model id");
	const requiresCredential = options.requiresCredential ?? false;
	const model: PiOpenAICompatibleModel = {
		id: modelId,
		contextWindow: options.contextWindow ?? 32_768,
		maximumOutputTokens: options.maximumOutputTokens ?? 4_096,
		reasoning: options.reasoning ?? false,
		input: [...(options.input ?? ["text"])],
		...(options.compatibility === undefined ? {} : { compatibility: options.compatibility }),
	};
	return {
		provider: {
			id: providerId,
			name: options.backend,
			baseUrl: localEndpoint(options.endpoint ?? backend.endpoint),
			protocol: options.protocol ?? backend.protocol,
			requiresCredential,
			...(requiresCredential ? {} : { anonymousApiKey: backend.anonymousApiKey }),
			models: [model],
		},
		profile: {
			id: profileId,
			provider: providerId,
			model: modelId,
			thinkingLevel: options.thinkingLevel ?? "off",
		},
	};
}
