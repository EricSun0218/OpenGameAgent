import { createHash } from "node:crypto";
import { UnauthorizedError } from "@modelcontextprotocol/sdk/client/auth.js";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPError } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { validateToolName } from "@modelcontextprotocol/sdk/shared/toolNameValidation.js";
import type { Transport } from "@modelcontextprotocol/sdk/shared/transport.js";
import {
	type CallToolResult,
	CallToolResultSchema,
	ErrorCode,
	type ListToolsResult,
	McpError,
	type Tool,
	type ToolAnnotations,
	ToolListChangedNotificationSchema,
} from "@modelcontextprotocol/sdk/types.js";
import type { JsonSchemaType, JsonSchemaValidator, jsonSchemaValidator } from "@modelcontextprotocol/sdk/validation";
import { AjvJsonSchemaValidator } from "@modelcontextprotocol/sdk/validation/ajv";
import {
	canonicalizeJsonValue,
	type JsonValue,
	JsonValueLimitError,
	normalizeJsonValueWithin,
} from "@opengameagent/protocol";
import { GameMcpError, type GameMcpErrorCode } from "./mcp-errors.js";
import type { GameMcpDiagnostic, GameMcpLimits, GameMcpServerDefinition, GameMcpToolSummary } from "./mcp-types.js";

const encoder = new TextEncoder();

export interface LoadedGameMcpTool {
	readonly summary: GameMcpToolSummary;
	readonly inputSchema: JsonValue;
	readonly outputSchema?: JsonValue;
	readonly executionTaskSupport: "required" | "optional" | "forbidden";
	readonly inputValidator: JsonSchemaValidator<Record<string, unknown>>;
	readonly outputValidator?: JsonSchemaValidator<Record<string, unknown>>;
	readonly fingerprint: string;
	readonly connection: GameMcpServerConnection;
	readonly connectionState: ConnectedGameMcpState;
	readonly catalogEpoch: bigint;
}

export interface LoadedGameMcpServerCatalog {
	readonly serverId: string;
	readonly connectionGeneration: bigint;
	readonly catalogEpoch: bigint;
	readonly revision: string;
	readonly tools: readonly LoadedGameMcpTool[];
}

export interface ConnectedGameMcpState {
	readonly client: Client;
	readonly transport: Transport;
	readonly generation: bigint;
	catalogEpoch: bigint;
	activeCalls: number;
}

export class GameMcpServerConnection implements AsyncDisposable {
	readonly definition: GameMcpServerDefinition;
	private readonly limits: GameMcpLimits;
	private readonly onDiagnostic: ((diagnostic: Readonly<GameMcpDiagnostic>) => void) | undefined;
	private state: ConnectedGameMcpState | undefined;
	private connecting: Promise<ConnectedGameMcpState> | undefined;
	private nextGeneration = 0n;
	private disposed = false;
	private readonly lifetime = new AbortController();

	constructor(
		definition: GameMcpServerDefinition,
		limits: GameMcpLimits,
		onDiagnostic?: (diagnostic: Readonly<GameMcpDiagnostic>) => void,
	) {
		this.definition = definition;
		this.limits = limits;
		this.onDiagnostic = onDiagnostic;
	}

	async loadCatalog(signal: AbortSignal): Promise<LoadedGameMcpServerCatalog> {
		const state = await this.ensureConnected(signal);
		if (!state.client.getServerCapabilities()?.tools) {
			throw new GameMcpError("server_capability_missing");
		}
		const epoch = state.catalogEpoch;
		const tools: LoadedGameMcpTool[] = [];
		const names = new Set<string>();
		const cursors = new Set<string>();
		let cursor: string | undefined;
		for (let page = 0; page < this.limits.maxListPages; page += 1) {
			signal.throwIfAborted();
			let result: ListToolsResult;
			try {
				result = await state.client.listTools(
					cursor === undefined ? undefined : { cursor },
					this.requestOptions(signal),
				);
			} catch (error) {
				if (signal.aborted) throw signal.reason;
				throw classifyMcpSdkFailure(error, "catalog_incomplete");
			}
			if (this.state !== state || state.catalogEpoch !== epoch) throw new GameMcpError("catalog_changed");
			for (const remote of result.tools) {
				if (tools.length >= this.limits.maxToolsPerServer) {
					throw new GameMcpError("catalog_limit_exceeded");
				}
				if (names.has(remote.name)) throw new GameMcpError("catalog_incomplete");
				names.add(remote.name);
				tools.push(this.normalizeTool(remote, state, epoch));
			}
			cursor = result.nextCursor;
			if (cursor === undefined) break;
			assertUtf8Bytes(cursor, this.limits.maxCursorBytes, "catalog_limit_exceeded");
			if (cursors.has(cursor)) throw new GameMcpError("catalog_incomplete");
			cursors.add(cursor);
			if (page + 1 === this.limits.maxListPages) throw new GameMcpError("catalog_limit_exceeded");
		}
		if (this.state !== state || state.catalogEpoch !== epoch) throw new GameMcpError("catalog_changed");
		const revision = sha256(
			canonicalizeJsonValue({
				serverId: this.definition.serverId,
				generation: state.generation.toString(),
				epoch: epoch.toString(),
				tools: tools.map((tool) => tool.fingerprint),
			}),
		);
		return {
			serverId: this.definition.serverId,
			connectionGeneration: state.generation,
			catalogEpoch: epoch,
			revision,
			tools,
		};
	}

	async call(
		tool: LoadedGameMcpTool,
		args: Readonly<Record<string, unknown>>,
		signal: AbortSignal,
	): Promise<CallToolResult> {
		const state = tool.connectionState;
		if (this.state !== state || state.catalogEpoch !== tool.catalogEpoch) {
			throw new GameMcpError("catalog_changed");
		}
		if (state.activeCalls >= this.limits.maxConcurrentCallsPerServer) {
			throw new GameMcpError("tool_call_limit_exceeded");
		}
		state.activeCalls += 1;
		try {
			let result: CallToolResult;
			try {
				result = (await state.client.callTool(
					{ name: tool.summary.name, arguments: args },
					CallToolResultSchema,
					this.requestOptions(signal, true),
				)) as CallToolResult;
			} catch (error) {
				if (signal.aborted) throw signal.reason;
				throw classifyMcpSdkFailure(error, "tool_unavailable");
			}
			if (tool.outputValidator !== undefined) {
				if (result.structuredContent === undefined) {
					if (result.isError !== true) throw new GameMcpError("tool_unavailable");
				} else if (!tool.outputValidator(result.structuredContent).valid) {
					throw new GameMcpError("tool_unavailable");
				}
			}
			return result;
		} finally {
			state.activeCalls -= 1;
		}
	}

	async close(): Promise<void> {
		if (this.disposed) return;
		this.disposed = true;
		this.lifetime.abort(new GameMcpError("server_unavailable"));
		const state = this.state;
		this.state = undefined;
		if (state) await state.client.close();
	}

	async [Symbol.asyncDispose](): Promise<void> {
		await this.close();
	}

	private async ensureConnected(signal: AbortSignal): Promise<ConnectedGameMcpState> {
		if (this.disposed) throw new GameMcpError("server_unavailable");
		signal.throwIfAborted();
		if (this.state) return this.state;
		if (!this.connecting) {
			const operation = this.open(this.lifetime.signal);
			this.connecting = operation;
			void operation.then(
				() => {
					if (this.connecting === operation) this.connecting = undefined;
				},
				() => {
					if (this.connecting === operation) this.connecting = undefined;
				},
			);
		}
		return await waitForShared(this.connecting, AbortSignal.any([signal, this.lifetime.signal]));
	}

	private async open(signal: AbortSignal): Promise<ConnectedGameMcpState> {
		let transport: Transport;
		try {
			transport = await this.definition.createTransport(signal);
		} catch (error) {
			if (signal.aborted) throw signal.reason;
			throw classifyMcpSdkFailure(error, "server_unavailable");
		}
		if (!isTransport(transport)) throw new GameMcpError("server_unavailable");
		const client = new Client(
			{ name: "OpenGameAgent", version: "0.1.0-alpha.0" },
			{ capabilities: {}, jsonSchemaValidator: isolatedValidatorProvider },
		);
		client.fallbackRequestHandler = async () => {
			throw new GameMcpError("tool_not_authorized");
		};
		const generation = this.nextGeneration + 1n;
		this.nextGeneration = generation;
		const state: ConnectedGameMcpState = { client, transport, generation, catalogEpoch: 0n, activeCalls: 0 };
		client.setNotificationHandler(ToolListChangedNotificationSchema, async () => {
			if (this.state !== state) return;
			state.catalogEpoch += 1n;
			this.report("tool_list_changed");
		});
		client.onerror = () => {
			if (this.state === state) this.report("connection_error");
		};
		client.onclose = () => {
			if (this.state !== state) return;
			this.state = undefined;
			this.report("connection_closed");
		};
		try {
			await client.connect(transport, this.requestOptions(signal));
		} catch (error) {
			await client.close().catch(() => undefined);
			if (signal.aborted) throw signal.reason;
			throw classifyMcpSdkFailure(error, "server_unavailable");
		}
		if (this.disposed) {
			await client.close();
			throw new GameMcpError("server_unavailable");
		}
		this.state = state;
		return state;
	}

	private normalizeTool(remote: Tool, state: ConnectedGameMcpState, epoch: bigint): LoadedGameMcpTool {
		if (!validateToolName(remote.name).isValid) throw new GameMcpError("catalog_incomplete");
		assertUtf8Bytes(remote.name, this.limits.maxToolNameBytes, "catalog_limit_exceeded");
		if (remote.title !== undefined) {
			assertUtf8Bytes(remote.title, this.limits.maxToolTitleBytes, "catalog_limit_exceeded");
		}
		if (remote.description !== undefined) {
			assertUtf8Bytes(remote.description, this.limits.maxToolDescriptionBytes, "catalog_limit_exceeded");
		}
		const inputSchema = normalizeSchema(remote.inputSchema, this.limits);
		const outputSchema =
			remote.outputSchema === undefined ? undefined : normalizeSchema(remote.outputSchema, this.limits);
		let inputValidator: JsonSchemaValidator<Record<string, unknown>>;
		let outputValidator: JsonSchemaValidator<Record<string, unknown>> | undefined;
		try {
			inputValidator = isolatedValidatorProvider.getValidator(inputSchema as JsonSchemaType);
			outputValidator =
				outputSchema === undefined ? undefined : isolatedValidatorProvider.getValidator(outputSchema as JsonSchemaType);
		} catch {
			throw new GameMcpError("invalid_tool_schema");
		}
		const annotations = normalizeAnnotations(remote.annotations);
		const summary: GameMcpToolSummary = Object.freeze({
			serverId: this.definition.serverId,
			serverDescription: this.definition.description,
			name: remote.name,
			...(remote.title === undefined ? {} : { title: remote.title }),
			...(remote.description === undefined ? {} : { description: remote.description }),
			...(annotations === undefined ? {} : { annotations }),
		});
		const executionTaskSupport = remote.execution?.taskSupport ?? "forbidden";
		const fingerprint = sha256(
			canonicalizeJsonValue({
				summary,
				inputSchema,
				...(outputSchema === undefined ? {} : { outputSchema }),
				executionTaskSupport,
			}),
		);
		return {
			summary,
			inputSchema,
			...(outputSchema === undefined ? {} : { outputSchema }),
			executionTaskSupport,
			inputValidator,
			...(outputValidator === undefined ? {} : { outputValidator }),
			fingerprint,
			connection: this,
			connectionState: state,
			catalogEpoch: epoch,
		};
	}

	private requestOptions(signal: AbortSignal, progress = false) {
		return {
			signal,
			timeout: this.limits.requestIdleTimeoutMs,
			maxTotalTimeout: this.limits.requestTotalTimeoutMs,
			resetTimeoutOnProgress: progress,
		};
	}

	private report(code: GameMcpDiagnostic["code"]): void {
		this.onDiagnostic?.({ serverId: this.definition.serverId, code });
	}
}

const isolatedValidatorProvider: jsonSchemaValidator = {
	getValidator<T>(schema: JsonSchemaType): JsonSchemaValidator<T> {
		return new AjvJsonSchemaValidator().getValidator<T>(schema);
	},
};

function normalizeSchema(schema: unknown, limits: GameMcpLimits): JsonValue {
	let normalized: JsonValue;
	try {
		normalized = normalizeJsonValueWithin(schema, {
			maxDepth: limits.maxSchemaDepth,
			maxBytes: limits.maxSchemaBytes,
		});
	} catch (error) {
		throw new GameMcpError(error instanceof JsonValueLimitError ? "catalog_limit_exceeded" : "invalid_tool_schema");
	}
	if (
		typeof normalized !== "object" ||
		normalized === null ||
		Array.isArray(normalized) ||
		normalized.type !== "object"
	) {
		throw new GameMcpError("invalid_tool_schema");
	}
	return normalized;
}

function normalizeAnnotations(value: ToolAnnotations | undefined): Readonly<ToolAnnotations> | undefined {
	if (value === undefined) return undefined;
	return Object.freeze({
		...(value.title === undefined ? {} : { title: value.title }),
		...(value.readOnlyHint === undefined ? {} : { readOnlyHint: value.readOnlyHint }),
		...(value.destructiveHint === undefined ? {} : { destructiveHint: value.destructiveHint }),
		...(value.idempotentHint === undefined ? {} : { idempotentHint: value.idempotentHint }),
		...(value.openWorldHint === undefined ? {} : { openWorldHint: value.openWorldHint }),
	});
}

function assertUtf8Bytes(value: string, maximum: number, code: "catalog_limit_exceeded"): void {
	if (encoder.encode(value).byteLength > maximum) throw new GameMcpError(code);
}

function isTransport(value: unknown): value is Transport {
	return (
		typeof value === "object" &&
		value !== null &&
		!Array.isArray(value) &&
		typeof (value as Transport).start === "function" &&
		typeof (value as Transport).send === "function" &&
		typeof (value as Transport).close === "function"
	);
}

function classifyMcpSdkFailure(error: unknown, fallback: GameMcpErrorCode): GameMcpError {
	if (error instanceof GameMcpError) return error;
	if (error instanceof UnauthorizedError || (error instanceof StreamableHTTPError && error.code === 401)) {
		return new GameMcpError("authentication_required");
	}
	if (error instanceof StreamableHTTPError && error.code === 403) {
		return new GameMcpError("permission_denied");
	}
	if (error instanceof McpError && error.code === ErrorCode.RequestTimeout) {
		return new GameMcpError("server_timeout");
	}
	if (error instanceof McpError && error.code === ErrorCode.ConnectionClosed) {
		return new GameMcpError("server_unavailable");
	}
	return new GameMcpError(fallback);
}

async function waitForShared<T>(operation: Promise<T>, signal: AbortSignal): Promise<T> {
	signal.throwIfAborted();
	return await new Promise<T>((resolve, reject) => {
		const abort = () => reject(signal.reason);
		signal.addEventListener("abort", abort, { once: true });
		operation.then(resolve, reject).finally(() => signal.removeEventListener("abort", abort));
	});
}

function sha256(value: string): string {
	return createHash("sha256").update(value).digest("hex");
}
