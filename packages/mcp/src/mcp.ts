import { createHash } from "node:crypto";
import type {
	GameInput,
	GameInputContent,
	GameTool,
	GameToolDefinition,
	GameToolResult,
	JsonObject,
	JsonValue,
} from "@opengameagent/protocol";
import { type GameToolProvider, preflightGameToolSchema } from "@opengameagent/runtime";
import type { GameMcpCallResult, GameMcpConnection, GameMcpRemoteTool } from "./client.js";

export type GameMcpToolExposure = "on-demand" | "direct";

export interface GameMcpServer {
	id: string;
	connect(signal?: AbortSignal): Promise<GameMcpConnection>;
	toolPrefix?: string;
	allowedTools?: readonly string[];
	risk?: GameToolDefinition["risk"];
	isVisible?(input: GameInput, signal: AbortSignal): Promise<boolean> | boolean;
}

export interface GameMcpDiagnostic {
	serverId: string;
	category: "connection" | "catalog" | "schema" | "call" | "lifecycle";
	message: string;
}

export interface GameMcpBridgeOptions {
	servers: readonly GameMcpServer[];
	exposure?: GameMcpToolExposure;
	continueOnServerFailure?: boolean;
	refreshMilliseconds?: number;
	maximumToolsPerServer?: number;
	maximumSchemaCharacters?: number;
	maximumDescriptionCharacters?: number;
	maximumResultCharacters?: number;
	maximumImageBytes?: number;
	maximumSearchResults?: number;
	toolCallTimeoutMilliseconds?: number;
	onDiagnostic?(diagnostic: GameMcpDiagnostic): void;
}

interface CatalogTool {
	raw: GameMcpRemoteTool;
	publicName: string;
	searchText: string;
}

interface ServerState {
	connection: GameMcpConnection | undefined;
	connectGeneration: number;
	catalog: readonly CatalogTool[];
	byRawName: ReadonlyMap<string, CatalogTool>;
	refreshAfter: number;
	loading: Promise<readonly CatalogTool[]> | undefined;
	unsubscribeChanged: (() => void) | undefined;
	unsubscribeClose: (() => void) | undefined;
}

const identifier = /^[A-Za-z0-9_.-]{1,128}$/u;
const base64 = /^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u;
const imageTypes = new Set(["image/png", "image/jpeg", "image/webp", "image/gif"]);

function boundedInteger(
	value: number | undefined,
	fallback: number,
	minimum: number,
	maximum: number,
	name: string,
): number {
	const result = value ?? fallback;
	if (!Number.isInteger(result) || result < minimum || result > maximum) throw new RangeError(`${name} is invalid.`);
	return result;
}

function publicToolName(server: GameMcpServer, rawName: string): string {
	const prefix = server.toolPrefix ?? `external_${server.id}_`;
	const joined = `${prefix}${rawName}`;
	const normalized = joined.replaceAll(/[^A-Za-z0-9_.:-]/gu, "_");
	if (normalized.length <= 128 && normalized === joined) return normalized;
	const digest = createHash("sha256").update(server.id).update("\0").update(rawName).digest("hex").slice(0, 16);
	return `${normalized.slice(0, 111)}_${digest}`;
}

function schemaText(schema: JsonObject): string {
	return JSON.stringify(schema);
}

function safeMessage(error: unknown): string {
	if (error instanceof DOMException && error.name === "AbortError") return "operation cancelled";
	if (error instanceof Error) return error.name;
	return "external tool operation failed";
}

function jsonObject(value: unknown, name: string): JsonObject {
	if (value === null || typeof value !== "object" || Array.isArray(value))
		throw new TypeError(`${name} must be an object.`);
	return value as JsonObject;
}

function isRecord(value: JsonValue): value is { [key: string]: JsonValue } {
	return value !== null && typeof value === "object" && !Array.isArray(value);
}

function textField(value: JsonValue | undefined, maximum: number): string | undefined {
	return typeof value === "string" && value.length <= maximum ? value : undefined;
}

function projectResult(
	result: GameMcpCallResult,
	maximumResultCharacters: number,
	maximumImageBytes: number,
): GameToolResult {
	const serialized = JSON.stringify(result);
	if (serialized.length > maximumResultCharacters)
		throw new RangeError("External tool result exceeds its configured limit.");
	const content: GameInputContent[] = [];
	for (const block of result.content) {
		if (!isRecord(block)) {
			content.push({ type: "text", text: "[unsupported external tool content]" });
			continue;
		}
		const type = block["type"];
		if (type === "text") {
			const text = textField(block["text"], maximumResultCharacters);
			content.push({ type: "text", text: text ?? "[invalid external text content]" });
			continue;
		}
		if (type === "image") {
			const mimeType = textField(block["mimeType"], 128);
			const data = textField(block["data"], Math.ceil((maximumImageBytes * 4) / 3) + 4);
			if (!mimeType || !imageTypes.has(mimeType) || !data || !base64.test(data)) {
				content.push({ type: "text", text: "[external image rejected]" });
				continue;
			}
			const bytes = Buffer.from(data, "base64");
			if (bytes.byteLength > maximumImageBytes || bytes.toString("base64") !== data) {
				content.push({ type: "text", text: "[external image rejected]" });
				continue;
			}
			content.push({ type: "image", mimeType, data });
			continue;
		}
		if (type === "resource_link") {
			const name = textField(block["name"], 512);
			const uri = textField(block["uri"], 2_048);
			content.push({
				type: "text",
				text: name && uri ? `External resource: ${name} (${uri})` : "[invalid external resource link]",
			});
			continue;
		}
		content.push({ type: "text", text: `[unsupported external content type: ${String(type)}]` });
	}
	if (content.length === 0 && result.structuredContent !== undefined)
		content.push({ type: "json", value: structuredClone(result.structuredContent) });
	if (content.length === 0) content.push({ type: "text", text: "(external tool returned no visible content)" });
	return {
		content,
		...(result.structuredContent === undefined ? {} : { details: structuredClone(result.structuredContent) }),
		...(result.isError === undefined ? {} : { isError: result.isError }),
	};
}

/**
 * Dynamic, engine-neutral MCP tool bridge. Tool discovery remains outside the
 * Agent loop; returned tools execute through the runtime's normal policy,
 * approval, tracing, cancellation, and durable-action boundaries.
 */
export class GameMcpToolBridge implements GameToolProvider, AsyncDisposable {
	readonly name = "mcp";
	private readonly states = new Map<string, ServerState>();
	private readonly exposure: GameMcpToolExposure;
	private readonly continueOnServerFailure: boolean;
	private readonly refreshMilliseconds: number;
	private readonly maximumToolsPerServer: number;
	private readonly maximumSchemaCharacters: number;
	private readonly maximumDescriptionCharacters: number;
	private readonly maximumResultCharacters: number;
	private readonly maximumImageBytes: number;
	private readonly maximumSearchResults: number;
	private readonly toolCallTimeoutMilliseconds: number;
	private readonly lifetime = new AbortController();
	private disposed = false;

	constructor(private readonly options: GameMcpBridgeOptions) {
		if (options.servers.length < 1 || options.servers.length > 1_000)
			throw new RangeError("MCP server count is invalid.");
		const serverIds = new Set<string>();
		const prefixes = new Set<string>();
		for (const server of options.servers) {
			if (!identifier.test(server.id) || serverIds.has(server.id))
				throw new Error("MCP server ids must be unique identifiers.");
			serverIds.add(server.id);
			const prefix = server.toolPrefix ?? `external_${server.id}_`;
			if (!prefix || prefix.length > 128 || prefixes.has(prefix))
				throw new Error("MCP tool prefixes must be unique and bounded.");
			prefixes.add(prefix);
			if (
				server.allowedTools &&
				(server.allowedTools.length > 10_000 || new Set(server.allowedTools).size !== server.allowedTools.length)
			)
				throw new Error("MCP allowed tool names are invalid.");
			this.states.set(server.id, {
				connection: undefined,
				connectGeneration: 0,
				catalog: [],
				byRawName: new Map(),
				refreshAfter: 0,
				loading: undefined,
				unsubscribeChanged: undefined,
				unsubscribeClose: undefined,
			});
		}
		this.exposure = options.exposure ?? "on-demand";
		if (this.exposure !== "on-demand" && this.exposure !== "direct") throw new Error("MCP exposure is invalid.");
		this.continueOnServerFailure = options.continueOnServerFailure ?? true;
		this.refreshMilliseconds = boundedInteger(
			options.refreshMilliseconds,
			300_000,
			0,
			86_400_000,
			"refreshMilliseconds",
		);
		this.maximumToolsPerServer = boundedInteger(options.maximumToolsPerServer, 256, 1, 10_000, "maximumToolsPerServer");
		this.maximumSchemaCharacters = boundedInteger(
			options.maximumSchemaCharacters,
			262_144,
			2,
			10_000_000,
			"maximumSchemaCharacters",
		);
		this.maximumDescriptionCharacters = boundedInteger(
			options.maximumDescriptionCharacters,
			16_384,
			0,
			1_000_000,
			"maximumDescriptionCharacters",
		);
		this.maximumResultCharacters = boundedInteger(
			options.maximumResultCharacters,
			1_000_000,
			1_024,
			100_000_000,
			"maximumResultCharacters",
		);
		this.maximumImageBytes = boundedInteger(
			options.maximumImageBytes,
			8_000_000,
			1_024,
			100_000_000,
			"maximumImageBytes",
		);
		this.maximumSearchResults = boundedInteger(options.maximumSearchResults, 50, 1, 1_000, "maximumSearchResults");
		this.toolCallTimeoutMilliseconds = boundedInteger(
			options.toolCallTimeoutMilliseconds,
			60_000,
			1,
			3_600_000,
			"toolCallTimeoutMilliseconds",
		);
	}

	async provide(input: GameInput, signal: AbortSignal): Promise<readonly GameTool[]> {
		this.assertOpen();
		const visible = await this.visibleServers(input, signal);
		if (this.exposure === "on-demand") return visible.length === 0 ? [] : [this.proxyTool(input, visible)];
		const catalogs = await Promise.all(
			visible.map(async (server) => {
				try {
					return { server, items: await this.catalog(server, signal) };
				} catch (error) {
					this.diagnostic(server.id, "catalog", safeMessage(error));
					if (!this.continueOnServerFailure) throw error;
					return { server, items: [] as readonly CatalogTool[] };
				}
			}),
		);
		const tools = catalogs.flatMap(({ server, items }) => items.map((item) => this.directTool(server, item)));
		return tools;
	}

	invalidate(serverId: string): void {
		const state = this.states.get(serverId);
		if (!state) throw new Error("MCP server is not configured.");
		state.refreshAfter = 0;
	}

	async [Symbol.asyncDispose](): Promise<void> {
		if (this.disposed) return;
		this.disposed = true;
		this.lifetime.abort();
		const closing: Promise<void>[] = [];
		for (const state of this.states.values()) {
			state.connectGeneration += 1;
			state.unsubscribeChanged?.();
			state.unsubscribeClose?.();
			if (state.connection) closing.push(state.connection.close().catch(() => undefined));
			state.connection = undefined;
			state.catalog = [];
			state.byRawName = new Map();
		}
		await Promise.all(closing);
	}

	private async visibleServers(input: GameInput, signal: AbortSignal): Promise<readonly GameMcpServer[]> {
		const result: GameMcpServer[] = [];
		for (const server of this.options.servers) {
			signal.throwIfAborted();
			if ((await server.isVisible?.(input, signal)) === false) continue;
			result.push(server);
		}
		return result;
	}

	private async catalog(server: GameMcpServer, signal: AbortSignal): Promise<readonly CatalogTool[]> {
		this.assertOpen();
		const state = this.states.get(server.id) as ServerState;
		if (state.connection && state.refreshAfter > Date.now()) return state.catalog;
		if (state.loading) return this.waitFor(state.loading, signal);
		const loadSignal = AbortSignal.any([this.lifetime.signal, AbortSignal.timeout(this.toolCallTimeoutMilliseconds)]);
		const loading = this.loadCatalog(server, state, loadSignal).finally(() => {
			if (state.loading === loading) state.loading = undefined;
		});
		state.loading = loading;
		return this.waitFor(loading, signal);
	}

	private async loadCatalog(
		server: GameMcpServer,
		state: ServerState,
		signal: AbortSignal,
	): Promise<readonly CatalogTool[]> {
		let connection = state.connection;
		if (!connection) {
			connection = await server.connect(signal);
			this.assertOpen();
			const generation = state.connectGeneration + 1;
			state.connectGeneration = generation;
			state.connection = connection;
			state.unsubscribeChanged = connection.onToolsChanged?.(() => {
				if (state.connectGeneration === generation) state.refreshAfter = 0;
			});
			state.unsubscribeClose = connection.onClose?.(() => {
				if (state.connectGeneration !== generation) return;
				state.unsubscribeChanged?.();
				state.unsubscribeChanged = undefined;
				state.unsubscribeClose = undefined;
				state.connection = undefined;
				state.catalog = [];
				state.byRawName = new Map();
				state.refreshAfter = 0;
				state.connectGeneration += 1;
			});
		}
		const generation = state.connectGeneration;
		const remote = await connection.listTools(signal);
		if (state.connection !== connection || state.connectGeneration !== generation)
			throw new Error("MCP connection changed during tool discovery.");
		if (remote.length > this.maximumToolsPerServer) throw new RangeError("MCP server exceeded its tool limit.");
		const seenRaw = new Set<string>();
		const seenPublic = new Set<string>();
		const allowed = server.allowedTools ? new Set(server.allowedTools) : undefined;
		const next: CatalogTool[] = [];
		for (const raw of remote) {
			if (allowed && !allowed.has(raw.name)) continue;
			if (!raw.name || raw.name.length > 512 || seenRaw.has(raw.name))
				throw new Error("MCP tool names are invalid or duplicated.");
			seenRaw.add(raw.name);
			const description = raw.description ?? "";
			if (description.length > this.maximumDescriptionCharacters)
				throw new RangeError("MCP tool description is too long.");
			const schema = schemaText(raw.inputSchema);
			if (schema.length > this.maximumSchemaCharacters) throw new RangeError("MCP tool schema is too large.");
			const publicName = publicToolName(server, raw.name);
			if (seenPublic.has(publicName)) throw new Error("MCP tool names collide after portable projection.");
			seenPublic.add(publicName);
			try {
				preflightGameToolSchema({
					name: publicName,
					label: raw.name.slice(0, 256),
					description,
					parameters: raw.inputSchema,
				});
			} catch (error) {
				this.diagnostic(server.id, "schema", `${raw.name}: ${safeMessage(error)}`);
				continue;
			}
			next.push({
				raw: structuredClone(raw),
				publicName,
				searchText: `${server.id} ${raw.name} ${description} ${schema}`.toLocaleLowerCase("en-US"),
			});
		}
		next.sort((left, right) => left.publicName.localeCompare(right.publicName));
		state.catalog = next;
		state.byRawName = new Map(next.map((item) => [item.raw.name, item]));
		state.refreshAfter = Date.now() + this.refreshMilliseconds;
		return state.catalog;
	}

	private directTool(server: GameMcpServer, item: CatalogTool): GameTool {
		return {
			definition: {
				name: item.publicName,
				label: item.raw.name.slice(0, 256),
				description: item.raw.description ?? "External game tool",
				parameters: structuredClone(item.raw.inputSchema),
				risk: server.risk ?? "high",
			},
			execute: (call, context) => this.invoke(server, item.raw.name, call.arguments, context.signal),
		};
	}

	private proxyTool(input: GameInput, servers: readonly GameMcpServer[]): GameTool {
		return {
			definition: {
				name: "use_external_game_tool",
				label: "Use external game tool",
				description: "Search, inspect, or call an authorized external tool. Search before calling an unfamiliar tool.",
				parameters: {
					type: "object",
					properties: {
						action: { type: "string", enum: ["search", "describe", "call"] },
						query: { type: "string", maxLength: 512 },
						server: { type: "string", maxLength: 128 },
						tool: { type: "string", maxLength: 512 },
						limit: { type: "integer", minimum: 1, maximum: this.maximumSearchResults },
						arguments: { type: "object" },
					},
					required: ["action"],
					additionalProperties: false,
				},
				risk: "high",
			},
			execute: async (call, context) => {
				if (context.input !== input) throw new Error("External tool scope expired.");
				const action = call.arguments["action"];
				if (action === "search") return this.search(servers, call.arguments, context.signal);
				if (action === "describe") return this.describe(servers, call.arguments, context.signal);
				if (action === "call") {
					const server = this.selectServer(servers, call.arguments["server"]);
					const tool = call.arguments["tool"];
					if (typeof tool !== "string") throw new TypeError("External tool name is required.");
					return this.invoke(server, tool, jsonObject(call.arguments["arguments"] ?? {}, "arguments"), context.signal);
				}
				throw new TypeError("External tool action is invalid.");
			},
		};
	}

	private async search(
		servers: readonly GameMcpServer[],
		argumentsValue: JsonObject,
		signal: AbortSignal,
	): Promise<GameToolResult> {
		const query = argumentsValue["query"];
		if (typeof query !== "string" || query.trim().length < 1) throw new TypeError("Search query is required.");
		const configuredLimit = argumentsValue["limit"] ?? 10;
		if (typeof configuredLimit !== "number" || !Number.isInteger(configuredLimit))
			throw new TypeError("Search limit is invalid.");
		const limit = Math.min(configuredLimit, this.maximumSearchResults);
		const terms = [
			...new Set(
				query
					.toLocaleLowerCase("en-US")
					.split(/[^\p{L}\p{N}_.:-]+/u)
					.filter(Boolean),
			),
		];
		const found = await Promise.all(
			servers.map(async (server) => {
				try {
					return (await this.catalog(server, signal)).flatMap((item) => {
						const score = terms.reduce((total, term) => total + (item.searchText.includes(term) ? 1 : 0), 0);
						return score > 0
							? [{ server: server.id, tool: item.raw.name, description: item.raw.description ?? "", score }]
							: [];
					});
				} catch (error) {
					this.diagnostic(server.id, "catalog", safeMessage(error));
					if (!this.continueOnServerFailure) throw error;
					return [];
				}
			}),
		);
		const matches = found.flat();
		matches.sort(
			(left, right) =>
				right.score - left.score || left.server.localeCompare(right.server) || left.tool.localeCompare(right.tool),
		);
		return { content: [{ type: "json", value: { matches: matches.slice(0, limit) } }] };
	}

	private async describe(
		servers: readonly GameMcpServer[],
		argumentsValue: JsonObject,
		signal: AbortSignal,
	): Promise<GameToolResult> {
		const server = this.selectServer(servers, argumentsValue["server"]);
		const tool = argumentsValue["tool"];
		if (typeof tool !== "string") throw new TypeError("External tool name is required.");
		const item = (await this.catalog(server, signal)).find((candidate) => candidate.raw.name === tool);
		if (!item) throw new Error("External tool is not available.");
		return {
			content: [
				{
					type: "json",
					value: {
						server: server.id,
						tool: item.raw.name,
						description: item.raw.description ?? "",
						parameters: structuredClone(item.raw.inputSchema),
					},
				},
			],
		};
	}

	private selectServer(servers: readonly GameMcpServer[], value: JsonValue | undefined): GameMcpServer {
		if (typeof value !== "string") throw new TypeError("External server id is required.");
		const server = servers.find((candidate) => candidate.id === value);
		if (!server) throw new Error("External server is not authorized for this input.");
		return server;
	}

	private async invoke(
		server: GameMcpServer,
		toolName: string,
		argumentsValue: JsonObject,
		signal: AbortSignal,
	): Promise<GameToolResult> {
		const state = this.states.get(server.id) as ServerState;
		await this.catalog(server, signal);
		if (!state.byRawName.has(toolName)) throw new Error("External tool is not available in the current catalog.");
		const connection = state.connection;
		if (!connection) throw new Error("External tool connection is not available.");
		const timeout = AbortSignal.timeout(this.toolCallTimeoutMilliseconds);
		const combined = AbortSignal.any([signal, timeout]);
		try {
			return projectResult(
				await connection.callTool(toolName, structuredClone(argumentsValue), combined),
				this.maximumResultCharacters,
				this.maximumImageBytes,
			);
		} catch (error) {
			this.diagnostic(server.id, "call", safeMessage(error));
			throw error;
		}
	}

	private diagnostic(serverId: string, category: GameMcpDiagnostic["category"], message: string): void {
		try {
			this.options.onDiagnostic?.({ serverId, category, message: message.slice(0, 1_024) });
		} catch {
			// Observation callbacks cannot alter tool availability or execution.
		}
	}

	private async waitFor<T>(promise: Promise<T>, signal: AbortSignal): Promise<T> {
		signal.throwIfAborted();
		return new Promise<T>((resolve, reject) => {
			const cancelled = () => reject(signal.reason);
			signal.addEventListener("abort", cancelled, { once: true });
			promise.then(resolve, reject).finally(() => signal.removeEventListener("abort", cancelled));
		});
	}

	private assertOpen(): void {
		if (this.disposed) throw new Error("MCP bridge is disposed.");
	}
}
