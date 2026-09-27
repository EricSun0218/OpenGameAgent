import type { OAuthClientProvider } from "@modelcontextprotocol/sdk/client/auth.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import type { Transport } from "@modelcontextprotocol/sdk/shared/transport.js";
import { createBoundedFetch } from "@opengameagent/client";
import { GameMcpError } from "./mcp-errors.js";
import { createMcpResourceScopedFetch } from "./mcp-scoped-fetch.js";
import { validateMcpSseRetryLine } from "./mcp-sse-retry.js";
import type { GameMcpTransportFactory } from "./mcp-types.js";
import { isAllowedMcpHttpUrl } from "./mcp-url.js";

const NODE_MAX_TIMER_DELAY_MS = 2_147_483_647;

export interface GameMcpStdioTransportOptions {
	readonly command: string;
	readonly args: readonly string[];
	readonly cwd?: string;
	readonly env: Readonly<Record<string, string>>;
	readonly stderr: "inherit" | "ignore";
	readonly maxMessageBytes: number;
}

export interface GameMcpHttpReconnectionPolicy {
	readonly initialDelayMs: number;
	readonly maxDelayMs: number;
	readonly delayGrowFactor: number;
	readonly maxAttempts: number;
}

export interface GameMcpStreamableHttpTransportOptions {
	readonly url: string;
	readonly headers?: ConstructorParameters<typeof Headers>[0];
	readonly authProvider?: OAuthClientProvider;
	readonly fetch?: typeof fetch;
	readonly maxHttpResponseBytes: number;
	readonly maxSseEventBytes: number;
	readonly reconnection: GameMcpHttpReconnectionPolicy;
}

export function createGameMcpStdioTransportFactory(options: GameMcpStdioTransportOptions): GameMcpTransportFactory {
	const prepared = prepareStdioOptions(options);
	return (signal) => {
		signal.throwIfAborted();
		return new StdioClientTransport({
			command: prepared.command,
			args: [...prepared.args],
			env: { ...prepared.env },
			stderr: prepared.stderr,
			maxBufferSize: prepared.maxMessageBytes,
			...(prepared.cwd === undefined ? {} : { cwd: prepared.cwd }),
		});
	};
}

export function createGameMcpStreamableHttpTransportFactory(
	options: GameMcpStreamableHttpTransportOptions,
): GameMcpTransportFactory {
	const prepared = prepareHttpOptions(options);
	const bounded = createBoundedFetch(
		prepared.fetch,
		prepared.maxHttpResponseBytes,
		prepared.maxSseEventBytes,
		() => new GameMcpError("transport_limit_exceeded"),
		undefined,
		(line, isFirstLine) => validateMcpSseRetryLine(line, prepared.reconnection.maxDelayMs, isFirstLine),
	);
	const scoped =
		prepared.headers === undefined ? bounded : createMcpResourceScopedFetch(bounded, prepared.url, prepared.headers);
	return (signal) => {
		signal.throwIfAborted();
		const transport = new StreamableHTTPClientTransport(new URL(prepared.url), {
			fetch: scoped,
			requestInit: {
				redirect: "error",
				credentials: "omit",
			},
			reconnectionOptions: {
				initialReconnectionDelay: prepared.reconnection.initialDelayMs,
				maxReconnectionDelay: prepared.reconnection.maxDelayMs,
				reconnectionDelayGrowFactor: prepared.reconnection.delayGrowFactor,
				maxRetries: prepared.reconnection.maxAttempts,
			},
			...(prepared.authProvider === undefined ? {} : { authProvider: prepared.authProvider }),
		});
		return transport as unknown as Transport;
	};
}

function prepareStdioOptions(options: GameMcpStdioTransportOptions) {
	if (typeof options !== "object" || options === null || Array.isArray(options)) {
		throw new GameMcpError("invalid_configuration");
	}
	const command = options.command;
	const inputArgs = options.args;
	const cwd = options.cwd;
	const inputEnv = options.env;
	const stderr = options.stderr;
	const maxMessageBytes = options.maxMessageBytes;
	const args = Array.isArray(inputArgs) ? [...inputArgs] : inputArgs;
	const envEntries =
		typeof inputEnv === "object" && inputEnv !== null && !Array.isArray(inputEnv)
			? Object.entries(inputEnv)
			: undefined;
	if (
		typeof command !== "string" ||
		command.length === 0 ||
		command.includes("\u0000") ||
		!Array.isArray(args) ||
		args.some((entry) => typeof entry !== "string" || entry.includes("\u0000")) ||
		(cwd !== undefined && (typeof cwd !== "string" || cwd.length === 0 || cwd.includes("\u0000"))) ||
		envEntries === undefined ||
		envEntries.some(
			([key, value]) =>
				key.length === 0 ||
				key.includes("=") ||
				key.includes("\u0000") ||
				typeof value !== "string" ||
				value.includes("\u0000"),
		) ||
		(stderr !== "inherit" && stderr !== "ignore") ||
		!isPositiveSafeInteger(maxMessageBytes)
	) {
		throw new GameMcpError("invalid_configuration");
	}
	return Object.freeze({
		command,
		args: Object.freeze(args),
		env: Object.freeze(Object.fromEntries(envEntries)),
		stderr,
		maxMessageBytes,
		...(cwd === undefined ? {} : { cwd }),
	});
}

function prepareHttpOptions(options: GameMcpStreamableHttpTransportOptions) {
	if (typeof options !== "object" || options === null || Array.isArray(options)) {
		throw new GameMcpError("invalid_configuration");
	}
	const inputUrl = options.url;
	const inputHeaders = options.headers;
	const authProvider = options.authProvider;
	const configuredFetch = options.fetch;
	const maxHttpResponseBytes = options.maxHttpResponseBytes;
	const maxSseEventBytes = options.maxSseEventBytes;
	const inputReconnection = options.reconnection;
	if (typeof inputReconnection !== "object" || inputReconnection === null || Array.isArray(inputReconnection)) {
		throw new GameMcpError("invalid_configuration");
	}
	const initialDelayMs = inputReconnection.initialDelayMs;
	const maxDelayMs = inputReconnection.maxDelayMs;
	const delayGrowFactor = inputReconnection.delayGrowFactor;
	const maxAttempts = inputReconnection.maxAttempts;
	let url: URL;
	try {
		url = new URL(inputUrl);
	} catch {
		throw new GameMcpError("invalid_configuration");
	}
	if (
		!isAllowedMcpHttpUrl(url) ||
		!isPositiveSafeInteger(maxHttpResponseBytes) ||
		!isPositiveSafeInteger(maxSseEventBytes) ||
		!isPositiveSafeInteger(initialDelayMs) ||
		!isPositiveSafeInteger(maxDelayMs) ||
		initialDelayMs > NODE_MAX_TIMER_DELAY_MS ||
		maxDelayMs > NODE_MAX_TIMER_DELAY_MS ||
		maxDelayMs < initialDelayMs ||
		!Number.isFinite(delayGrowFactor) ||
		delayGrowFactor < 1 ||
		!Number.isSafeInteger(maxAttempts) ||
		maxAttempts < 0
	) {
		throw new GameMcpError("invalid_configuration");
	}
	const upstream = configuredFetch ?? globalThis.fetch;
	if (typeof upstream !== "function") throw new GameMcpError("invalid_configuration");
	return Object.freeze({
		url,
		fetch: upstream,
		maxHttpResponseBytes,
		maxSseEventBytes,
		reconnection: Object.freeze({ initialDelayMs, maxDelayMs, delayGrowFactor, maxAttempts }),
		...(inputHeaders === undefined ? {} : { headers: new Headers(inputHeaders) }),
		...(authProvider === undefined ? {} : { authProvider }),
	});
}

function isPositiveSafeInteger(value: number): boolean {
	return Number.isSafeInteger(value) && value > 0;
}
