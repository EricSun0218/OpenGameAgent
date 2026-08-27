import { Client, StreamableHTTPClientTransport, type Transport } from "@modelcontextprotocol/client";
import { StdioClientTransport } from "@modelcontextprotocol/client/stdio";
import type { JsonObject, JsonValue } from "@opengameagent/protocol";

export interface GameMcpRemoteTool {
	name: string;
	description?: string;
	inputSchema: JsonObject;
}

export interface GameMcpCallResult {
	content: readonly JsonValue[];
	structuredContent?: JsonValue;
	isError?: boolean;
}

export interface GameMcpConnection {
	listTools(signal?: AbortSignal): Promise<readonly GameMcpRemoteTool[]>;
	callTool(name: string, argumentsValue: JsonObject, signal?: AbortSignal): Promise<GameMcpCallResult>;
	onToolsChanged?(handler: () => void): () => void;
	onClose?(handler: () => void): () => void;
	close(): Promise<void>;
}

export interface GameMcpSdkConnectionOptions {
	name?: string;
	version?: string;
	maximumListPages?: number;
	requestTimeoutMilliseconds?: number;
	createTransport(signal?: AbortSignal): Promise<Transport> | Transport;
}

function jsonValue(value: unknown, path = "result", depth = 0): JsonValue {
	if (depth > 64) throw new RangeError(`${path} exceeds the supported JSON depth.`);
	if (value === null || typeof value === "string" || typeof value === "boolean") return value;
	if (typeof value === "number") {
		if (!Number.isFinite(value)) throw new TypeError(`${path} contains a non-finite number.`);
		return value;
	}
	if (Array.isArray(value)) return value.map((item, index) => jsonValue(item, `${path}[${index}]`, depth + 1));
	if (typeof value === "object") {
		const result: Record<string, JsonValue> = {};
		for (const [key, item] of Object.entries(value)) result[key] = jsonValue(item, `${path}.${key}`, depth + 1);
		return result;
	}
	throw new TypeError(`${path} is not JSON-compatible.`);
}

function positiveInteger(value: number | undefined, fallback: number, maximum: number, name: string): number {
	const result = value ?? fallback;
	if (!Number.isInteger(result) || result < 1 || result > maximum) throw new RangeError(`${name} is invalid.`);
	return result;
}

/**
 * Thin adapter over the official protocol client. The rest of OpenGameAgent
 * depends only on GameMcpConnection, which keeps transports replaceable in
 * engine processes, sidecars, and servers.
 */
export async function connectGameMcpSdk(options: GameMcpSdkConnectionOptions): Promise<GameMcpConnection> {
	const maximumListPages = positiveInteger(options.maximumListPages, 64, 1_024, "maximumListPages");
	const requestTimeoutMilliseconds = positiveInteger(
		options.requestTimeoutMilliseconds,
		60_000,
		3_600_000,
		"requestTimeoutMilliseconds",
	);
	const changed = new Set<() => void>();
	const closed = new Set<() => void>();
	let isClosed = false;
	const client = new Client(
		{ name: options.name ?? "opengameagent", version: options.version ?? "0.4.0" },
		{
			listMaxPages: maximumListPages,
			inputRequired: { autoFulfill: false },
			listChanged: {
				tools: {
					onChanged: (error) => {
						if (error) return;
						for (const handler of [...changed]) handler();
					},
				},
			},
		},
	);
	client.onclose = () => {
		if (isClosed) return;
		isClosed = true;
		for (const handler of [...closed]) handler();
	};
	const transport = await options.createTransport();
	await client.connect(transport);
	return {
		async listTools(signal) {
			signal?.throwIfAborted();
			const listed = await client.listTools(undefined, {
				...(signal === undefined ? {} : { signal }),
				timeout: requestTimeoutMilliseconds,
				maxTotalTimeout: requestTimeoutMilliseconds,
			});
			return listed.tools.map((tool) => {
				const schema = jsonValue(tool.inputSchema, `tool.${tool.name}.inputSchema`);
				if (schema === null || typeof schema !== "object" || Array.isArray(schema))
					throw new TypeError(`Tool '${tool.name}' input schema is not an object.`);
				return {
					name: tool.name,
					...(tool.description === undefined ? {} : { description: tool.description }),
					inputSchema: schema,
				};
			});
		},
		async callTool(name, argumentsValue, signal) {
			signal?.throwIfAborted();
			const result = await client.callTool(
				{ name, arguments: argumentsValue },
				{
					...(signal === undefined ? {} : { signal }),
					timeout: requestTimeoutMilliseconds,
					maxTotalTimeout: requestTimeoutMilliseconds,
				},
			);
			const content = jsonValue(result.content, "tool.content");
			if (!Array.isArray(content)) throw new TypeError("Tool content is not an array.");
			return {
				content,
				...(result.structuredContent === undefined
					? {}
					: { structuredContent: jsonValue(result.structuredContent, "tool.structuredContent") }),
				...(result.isError === undefined ? {} : { isError: result.isError }),
			};
		},
		onToolsChanged(handler) {
			changed.add(handler);
			return () => changed.delete(handler);
		},
		onClose(handler) {
			closed.add(handler);
			return () => closed.delete(handler);
		},
		async close() {
			if (isClosed) return;
			isClosed = true;
			changed.clear();
			closed.clear();
			await client.close();
		},
	};
}

export interface GameMcpHttpConnectionOptions extends Omit<GameMcpSdkConnectionOptions, "createTransport"> {
	endpoint: string | URL;
	headers?: Readonly<Record<string, string>>;
	allowInsecureLocalhost?: boolean;
}

function trustedMcpEndpoint(value: string | URL, allowInsecureLocalhost: boolean): URL {
	const endpoint = new URL(value);
	if (endpoint.username || endpoint.password) throw new Error("MCP endpoints cannot contain user information.");
	if (endpoint.protocol === "https:") return endpoint;
	const local = endpoint.hostname === "localhost" || endpoint.hostname === "127.0.0.1" || endpoint.hostname === "::1";
	if (endpoint.protocol === "http:" && allowInsecureLocalhost && local) return endpoint;
	throw new Error("MCP endpoints require HTTPS; HTTP is allowed only for an explicitly trusted loopback server.");
}

export function connectHttpGameMcp(options: GameMcpHttpConnectionOptions): Promise<GameMcpConnection> {
	const endpoint = trustedMcpEndpoint(options.endpoint, options.allowInsecureLocalhost === true);
	const headers = new Headers();
	for (const [name, value] of Object.entries(options.headers ?? {})) {
		if (!name || name.length > 256 || /[\r\n\0]/u.test(name) || value.length > 65_536 || /[\r\n\0]/u.test(value))
			throw new Error("MCP HTTP headers are invalid.");
		headers.set(name, value);
	}
	return connectGameMcpSdk({
		...options,
		createTransport: () =>
			new StreamableHTTPClientTransport(endpoint, {
				requestInit: { headers, redirect: "error", credentials: "omit" },
			}),
	});
}

export interface GameMcpStdioConnectionOptions extends Omit<GameMcpSdkConnectionOptions, "createTransport"> {
	command: string;
	arguments?: readonly string[];
	environment?: Readonly<Record<string, string>>;
	workingDirectory?: string;
	maximumMessageBytes?: number;
}

export function connectStdioGameMcp(options: GameMcpStdioConnectionOptions): Promise<GameMcpConnection> {
	if (!options.command || options.command.length > 32_768 || options.command.includes("\0"))
		throw new Error("The MCP process command is invalid.");
	const args = [...(options.arguments ?? [])];
	if (args.length > 1_024 || args.some((value) => value.length > 65_536 || value.includes("\0")))
		throw new Error("MCP process arguments exceed their configured bounds.");
	const env: Record<string, string> = {};
	for (const [name, value] of Object.entries(options.environment ?? {})) {
		if (!name || name.length > 512 || /[=\0]/u.test(name) || value.length > 65_536 || value.includes("\0"))
			throw new Error("MCP process environment is invalid.");
		env[name] = value;
	}
	const maximumMessageBytes = positiveInteger(
		options.maximumMessageBytes,
		10_000_000,
		100_000_000,
		"maximumMessageBytes",
	);
	return connectGameMcpSdk({
		...options,
		createTransport: () =>
			new StdioClientTransport({
				command: options.command,
				args,
				env,
				...(options.workingDirectory === undefined ? {} : { cwd: options.workingDirectory }),
				maxBufferSize: maximumMessageBytes,
				stderr: "pipe",
			}),
	});
}
