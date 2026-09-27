import { createHash } from "node:crypto";
import type { AgentTool, AgentToolResult } from "@earendil-works/pi-agent-core";
import { validateToolName } from "@modelcontextprotocol/sdk/shared/toolNameValidation.js";
import type { CallToolResult, ContentBlock } from "@modelcontextprotocol/sdk/types.js";
import type { GameRequestContribution, GameRequestSource, NormalizedGameInput } from "@opengameagent/agent";
import type { RuntimeDependencyDefinition, RuntimeDependencyInspection } from "@opengameagent/health";
import {
	assertOpaqueIdentifier,
	canonicalizeJsonValue,
	type JsonValue,
	normalizeJsonValueWithin,
	type RunCoordinate,
} from "@opengameagent/protocol";
import { type Static, type TSchema, Type } from "typebox";
import Queue from "yocto-queue";
import { GameMcpServerConnection, type LoadedGameMcpServerCatalog, type LoadedGameMcpTool } from "./mcp-client.js";
import { GameMcpError, GameMcpToolReportedError } from "./mcp-errors.js";
import type {
	GameMcpCatalogInspection,
	GameMcpDiagnostic,
	GameMcpLimits,
	GameMcpRequestSourceOptions,
	GameMcpToolSelection,
	GameMcpToolSummary,
} from "./mcp-types.js";

const encoder = new TextEncoder();
const MCP_LIMIT_NAMES = [
	"maxServers",
	"maxServerIdBytes",
	"maxServerDescriptionBytes",
	"maxListPages",
	"maxCursorBytes",
	"maxToolsPerServer",
	"maxDiagnostics",
	"maxToolNameBytes",
	"maxToolTitleBytes",
	"maxToolDescriptionBytes",
	"maxSchemaDepth",
	"maxSchemaBytes",
	"maxCatalogBytes",
	"maxSearchQueryBytes",
	"maxSearchResults",
	"maxCallArgumentDepth",
	"maxCallArgumentBytes",
	"maxResultContentBlocks",
	"maxResultTextBytes",
	"maxResultMediaBytes",
	"maxResultUriBytes",
	"maxResultMimeTypeBytes",
	"maxStructuredContentDepth",
	"maxStructuredContentBytes",
	"maxToolResultBytes",
	"maxConcurrentCallsPerServer",
	"requestIdleTimeoutMs",
	"requestTotalTimeoutMs",
] as const satisfies readonly (keyof GameMcpLimits)[];

export interface GameMcpToolDetails {
	readonly kind: "mcp";
	readonly serverId: string;
	readonly name: string;
	readonly catalogRevision: string;
	readonly contentKinds: readonly string[];
	readonly structuredContent?: JsonValue;
}

interface SelectedGameMcpTool {
	readonly tool: LoadedGameMcpTool;
	readonly selection: GameMcpToolSelection;
}

interface LoadedGameMcpCatalog {
	readonly revision: string;
	readonly tools: readonly LoadedGameMcpTool[];
}

export class GameMcpRequestSource implements GameRequestSource, AsyncDisposable {
	readonly provenanceKind = "mcp" as const;
	readonly sourceId: string;
	private readonly limits: GameMcpLimits;
	private readonly connections: readonly GameMcpServerConnection[];
	private readonly healthLifetime = new AbortController();
	private readonly lazyToolNames: GameMcpRequestSourceOptions["lazyToolNames"];
	private readonly select: GameMcpRequestSourceOptions["select"];
	private readonly authorizeCall: GameMcpRequestSourceOptions["authorizeCall"];
	private readonly onDiagnostic: GameMcpRequestSourceOptions["onDiagnostic"];
	private readonly diagnostics = new Queue<GameMcpDiagnostic>();
	private disposed = false;

	constructor(inputOptions: GameMcpRequestSourceOptions) {
		const options = prepareOptions(inputOptions);
		this.sourceId = options.sourceId;
		this.limits = options.limits;
		this.lazyToolNames = options.lazyToolNames;
		this.select = options.select;
		this.authorizeCall = options.authorizeCall;
		this.onDiagnostic = options.onDiagnostic;
		this.connections = options.servers.map(
			(server) =>
				new GameMcpServerConnection({ ...server }, this.limits, (diagnostic) => this.recordDiagnostic(diagnostic)),
		);
	}

	async collect(
		input: Readonly<NormalizedGameInput>,
		signal: AbortSignal,
		coordinate: Readonly<RunCoordinate>,
		visibleToolNames: readonly string[],
	): Promise<GameRequestContribution> {
		if (this.disposed) throw new GameMcpError("server_unavailable");
		signal.throwIfAborted();
		const requestInput = structuredClone(input);
		deepFreezeJson(requestInput as JsonValue);
		const requestCoordinate = Object.freeze({ ...coordinate });
		const admittedToolNames = Object.freeze([...visibleToolNames]);
		const catalog = await this.loadCatalog(signal);
		const eligible = catalog.tools.filter((tool) => tool.executionTaskSupport !== "required");
		const candidates = Object.freeze(eligible.map((tool) => tool.summary));
		let rawSelections: readonly GameMcpToolSelection[];
		try {
			rawSelections = await this.select(
				{
					input: requestInput,
					coordinate: requestCoordinate,
					visibleToolNames: admittedToolNames,
					candidates,
				},
				signal,
			);
		} catch {
			if (signal.aborted) throw signal.reason;
			throw new GameMcpError("invalid_selection");
		}
		signal.throwIfAborted();
		const selected = this.validateSelection(rawSelections, eligible, admittedToolNames);
		if (selected.length === 0) return {};
		const revision = selectedRevision(selected);
		const lazy = selected.filter((entry) => entry.selection.exposure === "lazy");
		const direct = selected.filter((entry) => entry.selection.exposure === "direct");
		if (lazy.length > 0 && Object.values(this.lazyToolNames).some((name) => admittedToolNames.includes(name))) {
			throw new GameMcpError("invalid_selection");
		}
		const tools: AgentTool[] = direct.map((entry) =>
			this.createDirectTool(entry, requestInput, requestCoordinate, revision),
		);
		if (lazy.length > 0) tools.push(...this.createLazyTools(lazy, requestInput, requestCoordinate, revision));
		const content = {
			catalogVersion: "1",
			kind: "available-mcp-tools",
			catalogRevision: revision,
			servers: uniqueServers(selected).map((entry) => ({
				serverId: entry.serverId,
				description: entry.serverDescription,
			})),
			...(lazy.length === 0
				? {}
				: {
						lazy: {
							searchTool: this.lazyToolNames.search,
							describeTool: this.lazyToolNames.describe,
							count: lazy.length,
						},
					}),
			direct: direct.map((entry) => ({
				modelToolName: entry.selection.exposure === "direct" ? entry.selection.modelToolName : "",
				serverId: entry.tool.summary.serverId,
				name: entry.tool.summary.name,
			})),
		};
		assertBytes(canonicalizeJsonValue(content), this.limits.maxCatalogBytes, "catalog_limit_exceeded");
		return {
			context: { references: [{ referenceId: "catalog", revision, content }] },
			tools,
		};
	}

	async inspect(signal: AbortSignal): Promise<GameMcpCatalogInspection> {
		const catalog = await this.loadCatalog(signal);
		return {
			revision: catalog.revision,
			tools: catalog.tools.map((tool) => tool.summary),
			diagnostics: [...this.diagnostics].map((entry) => ({ ...entry })),
		};
	}

	healthDependencies(): readonly RuntimeDependencyDefinition[] {
		return this.connections.map((connection) => ({
			id: connection.definition.serverId,
			kind: "mcp",
			requirement: connection.definition.requirement,
			inspect: async (callerSignal) => {
				const probe = new GameMcpServerConnection(connection.definition, this.limits);
				const signal = AbortSignal.any([callerSignal, this.healthLifetime.signal]);
				try {
					const catalog = await probe.loadCatalog(signal);
					assertCatalogBytes(catalog.tools, this.limits.maxCatalogBytes);
					return { state: "ready" };
				} catch (error) {
					if (callerSignal.aborted) throw callerSignal.reason;
					return mapMcpHealthFailure(error);
				} finally {
					await probe.close().catch(() => undefined);
				}
			},
		}));
	}

	async close(): Promise<void> {
		if (this.disposed) return;
		this.disposed = true;
		this.healthLifetime.abort(new GameMcpError("server_unavailable"));
		let failed = false;
		for (const connection of this.connections) {
			try {
				await connection.close();
			} catch {
				failed = true;
			}
		}
		if (failed) throw new GameMcpError("server_unavailable");
	}

	async [Symbol.asyncDispose](): Promise<void> {
		await this.close();
	}

	private async loadCatalog(signal: AbortSignal): Promise<LoadedGameMcpCatalog> {
		const catalogs: LoadedGameMcpServerCatalog[] = [];
		for (const connection of this.connections) {
			try {
				const catalog = await connection.loadCatalog(signal);
				catalogs.push(catalog);
				for (const tool of catalog.tools) {
					if (tool.executionTaskSupport === "required") {
						this.recordDiagnostic({
							serverId: connection.definition.serverId,
							code: "task_required_tool_ignored",
						});
					}
				}
			} catch (error) {
				if (signal.aborted) throw signal.reason;
				if (connection.definition.requirement === "required") {
					throw error instanceof GameMcpError ? error : new GameMcpError("server_unavailable");
				}
				this.recordDiagnostic({
					serverId: connection.definition.serverId,
					code: "optional_server_unavailable",
				});
			}
		}
		const tools = catalogs.flatMap((catalog) => catalog.tools);
		assertCatalogBytes(tools, this.limits.maxCatalogBytes);
		return {
			revision: sha256(
				canonicalizeJsonValue(
					catalogs.map((catalog) => ({
						serverId: catalog.serverId,
						revision: catalog.revision,
					})),
				),
			),
			tools,
		};
	}

	private validateSelection(
		selections: readonly GameMcpToolSelection[],
		eligible: readonly LoadedGameMcpTool[],
		visibleToolNames: readonly string[],
	): readonly SelectedGameMcpTool[] {
		if (!Array.isArray(selections) || selections.length > eligible.length) {
			throw new GameMcpError("invalid_selection");
		}
		const byKey = new Map(eligible.map((tool) => [toolKey(tool.summary.serverId, tool.summary.name), tool]));
		const selected: SelectedGameMcpTool[] = [];
		const remoteKeys = new Set<string>();
		const modelNames = new Set<string>([...visibleToolNames, ...Object.values(this.lazyToolNames)]);
		for (const selection of selections) {
			if (typeof selection !== "object" || selection === null || Array.isArray(selection)) {
				throw new GameMcpError("invalid_selection");
			}
			const key = toolKey(selection.serverId, selection.name);
			const tool = byKey.get(key);
			if (!tool || remoteKeys.has(key)) throw new GameMcpError("invalid_selection");
			if (selection.executionMode !== "parallel" && selection.executionMode !== "sequential") {
				throw new GameMcpError("invalid_selection");
			}
			if (selection.exposure === "direct") {
				if (!validateToolName(selection.modelToolName).isValid || modelNames.has(selection.modelToolName)) {
					throw new GameMcpError("invalid_selection");
				}
				assertBytes(selection.modelToolName, this.limits.maxToolNameBytes, "invalid_selection");
				modelNames.add(selection.modelToolName);
			} else if (selection.exposure !== "lazy") {
				throw new GameMcpError("invalid_selection");
			}
			remoteKeys.add(key);
			selected.push({ tool, selection: Object.freeze({ ...selection }) });
		}
		return selected;
	}

	private createDirectTool(
		entry: SelectedGameMcpTool,
		input: Readonly<NormalizedGameInput>,
		coordinate: Readonly<RunCoordinate>,
		revision: string,
	): AgentTool {
		if (entry.selection.exposure !== "direct") throw new GameMcpError("invalid_selection");
		return {
			name: entry.selection.modelToolName,
			label: entry.tool.summary.title ?? entry.tool.summary.name,
			description:
				entry.tool.summary.description ?? `Call ${entry.tool.summary.name} on ${entry.tool.summary.serverId}.`,
			parameters: entry.tool.inputSchema as TSchema,
			executionMode: entry.selection.executionMode,
			execute: async (toolCallId, parameters, signal) =>
				await this.executeRemote(
					entry.tool,
					parameters as Record<string, unknown>,
					false,
					input,
					coordinate,
					toolCallId,
					revision,
					signal ?? new AbortController().signal,
				),
		};
	}

	private createLazyTools(
		entries: readonly SelectedGameMcpTool[],
		input: Readonly<NormalizedGameInput>,
		coordinate: Readonly<RunCoordinate>,
		revision: string,
	): readonly AgentTool[] {
		const byKey = new Map(
			entries.map((entry) => [toolKey(entry.tool.summary.serverId, entry.tool.summary.name), entry]),
		);
		const locator = {
			serverId: Type.String({ maxLength: this.limits.maxServerIdBytes }),
			name: Type.String({ maxLength: 128 }),
			catalogRevision: Type.String({ pattern: "^[0-9a-f]{64}$" }),
		};
		const searchParameters = Type.Object(
			{
				query: Type.String({ maxLength: this.limits.maxSearchQueryBytes }),
				maxResults: Type.Integer({ minimum: 1, maximum: this.limits.maxSearchResults }),
				catalogRevision: locator.catalogRevision,
			},
			{ additionalProperties: false },
		);
		const describeParameters = Type.Object(locator, { additionalProperties: false });
		const callParameters = Type.Object(
			{ ...locator, arguments: Type.Record(Type.String(), Type.Unknown()) },
			{ additionalProperties: false },
		);
		const callToolNames = {
			parallel: this.lazyToolNames.callParallel,
			sequential: this.lazyToolNames.callSequential,
		};
		return [
			{
				name: this.lazyToolNames.search,
				label: "Search MCP tools",
				description: "Search the currently authorized MCP Tool catalog before describing or calling an exact match.",
				parameters: searchParameters,
				executionMode: "parallel",
				execute: async (_toolCallId, parameters, signal) => {
					const request = parameters as Static<typeof searchParameters>;
					const operation = signal ?? new AbortController().signal;
					operation.throwIfAborted();
					this.assertRevision(request.catalogRevision, revision);
					assertBytes(request.query, this.limits.maxSearchQueryBytes, "catalog_limit_exceeded");
					const terms = request.query.trim().toLowerCase().split(/\s+/u).filter(Boolean);
					if (terms.length === 0) throw new GameMcpError("invalid_tool_arguments");
					const matches = entries
						.filter((entry) => terms.every((term) => searchText(entry.tool.summary).includes(term)))
						.slice(0, request.maxResults)
						.map((entry) => ({
							serverId: entry.tool.summary.serverId,
							name: entry.tool.summary.name,
							...(entry.tool.summary.title === undefined ? {} : { title: entry.tool.summary.title }),
							...(entry.tool.summary.description === undefined ? {} : { description: entry.tool.summary.description }),
						}));
					return this.jsonToolResult({ catalogRevision: revision, matches });
				},
			},
			{
				name: this.lazyToolNames.describe,
				label: "Describe MCP tool",
				description: "Load the exact input/output schema and untrusted annotations for one authorized MCP Tool.",
				parameters: describeParameters,
				executionMode: "parallel",
				execute: async (_toolCallId, parameters, signal) => {
					const request = parameters as Static<typeof describeParameters>;
					(signal ?? new AbortController().signal).throwIfAborted();
					this.assertRevision(request.catalogRevision, revision);
					const entry = byKey.get(toolKey(request.serverId, request.name));
					if (!entry) throw new GameMcpError("tool_unavailable");
					return this.jsonToolResult({
						catalogRevision: revision,
						...entry.tool.summary,
						inputSchema: entry.tool.inputSchema,
						...(entry.tool.outputSchema === undefined ? {} : { outputSchema: entry.tool.outputSchema }),
						executionMode: entry.selection.executionMode,
						callTool: callToolNames[entry.selection.executionMode],
					});
				},
			},
			...[...new Set(entries.map((entry) => entry.selection.executionMode))].map(
				(executionMode): AgentTool<typeof callParameters> => ({
					name: callToolNames[executionMode],
					label: `Call MCP tool (${executionMode})`,
					description: `Call one exact authorized ${executionMode} MCP Tool using the callTool returned by describe.`,
					parameters: callParameters,
					executionMode,
					execute: async (toolCallId, request, signal) => {
						this.assertRevision(request.catalogRevision, revision);
						const entry = byKey.get(toolKey(request.serverId, request.name));
						if (!entry || entry.selection.executionMode !== executionMode) throw new GameMcpError("tool_unavailable");
						return await this.executeRemote(
							entry.tool,
							request.arguments,
							true,
							input,
							coordinate,
							toolCallId,
							revision,
							signal ?? new AbortController().signal,
						);
					},
				}),
			),
		];
	}

	private async executeRemote(
		tool: LoadedGameMcpTool,
		parameters: Readonly<Record<string, unknown>>,
		validateRemoteArguments: boolean,
		input: Readonly<NormalizedGameInput>,
		coordinate: Readonly<RunCoordinate>,
		toolCallId: string,
		revision: string,
		signal: AbortSignal,
	): Promise<AgentToolResult<GameMcpToolDetails>> {
		signal.throwIfAborted();
		let normalized: JsonValue;
		try {
			normalized = normalizeJsonValueWithin(parameters, {
				maxDepth: this.limits.maxCallArgumentDepth,
				maxBytes: this.limits.maxCallArgumentBytes,
			});
		} catch {
			throw new GameMcpError("invalid_tool_arguments");
		}
		if (typeof normalized !== "object" || normalized === null || Array.isArray(normalized)) {
			throw new GameMcpError("invalid_tool_arguments");
		}
		deepFreezeJson(normalized);
		if (validateRemoteArguments && !tool.inputValidator(normalized).valid) {
			throw new GameMcpError("invalid_tool_arguments");
		}
		try {
			await this.authorizeCall(
				{
					input,
					coordinate,
					toolCallId,
					serverId: tool.summary.serverId,
					name: tool.summary.name,
					arguments: normalized,
					...(tool.summary.annotations === undefined ? {} : { annotations: tool.summary.annotations }),
				},
				signal,
			);
		} catch {
			if (signal.aborted) throw signal.reason;
			throw new GameMcpError("tool_not_authorized");
		}
		signal.throwIfAborted();
		const result = await tool.connection.call(tool, normalized, signal);
		const projected = projectResult(result, this.limits, tool.summary.serverId, tool.summary.name, revision);
		if (result.isError === true) {
			throw new GameMcpToolReportedError(projected);
		}
		return projected;
	}

	private jsonToolResult(value: unknown): AgentToolResult<Record<string, never>> {
		let normalized: JsonValue;
		try {
			normalized = normalizeJsonValueWithin(value, {
				maxDepth: this.limits.maxSchemaDepth,
				maxBytes: this.limits.maxToolResultBytes,
			});
		} catch {
			throw new GameMcpError("result_limit_exceeded");
		}
		const text = canonicalizeJsonValue(normalized);
		assertBytes(text, this.limits.maxToolResultBytes, "result_limit_exceeded");
		return { content: [{ type: "text", text }], details: {} };
	}

	private assertRevision(actual: string, expected: string): void {
		if (actual !== expected) throw new GameMcpError("catalog_changed");
	}

	private recordDiagnostic(diagnostic: Readonly<GameMcpDiagnostic>): void {
		this.diagnostics.enqueue({ ...diagnostic });
		if (this.diagnostics.size > this.limits.maxDiagnostics) this.diagnostics.dequeue();
		try {
			this.onDiagnostic?.({ ...diagnostic });
		} catch {
			// Diagnostics are observational and cannot change protocol or authorization behavior.
		}
	}
}

function projectResult(
	result: CallToolResult,
	limits: GameMcpLimits,
	serverId: string,
	name: string,
	revision: string,
): AgentToolResult<GameMcpToolDetails> {
	if (result.content.length > limits.maxResultContentBlocks) throw new GameMcpError("result_limit_exceeded");
	const content: AgentToolResult<GameMcpToolDetails>["content"] = [];
	const kinds: string[] = [];
	for (const block of result.content) {
		projectBlock(block, limits, content, kinds);
	}
	let structuredContent: JsonValue | undefined;
	if (result.structuredContent !== undefined) {
		try {
			structuredContent = normalizeJsonValueWithin(result.structuredContent, {
				maxDepth: limits.maxStructuredContentDepth,
				maxBytes: limits.maxStructuredContentBytes,
			});
		} catch {
			throw new GameMcpError("result_limit_exceeded");
		}
		const rendered = `<mcp_structured_content>${canonicalizeJsonValue(structuredContent)}</mcp_structured_content>`;
		assertBytes(rendered, limits.maxResultTextBytes, "result_limit_exceeded");
		content.push({ type: "text", text: rendered });
		kinds.push("structured");
	}
	const details: GameMcpToolDetails = {
		kind: "mcp",
		serverId,
		name,
		catalogRevision: revision,
		contentKinds: kinds,
		...(structuredContent === undefined ? {} : { structuredContent }),
	};
	assertBytes(canonicalizeJsonValue({ content, details }), limits.maxToolResultBytes, "result_limit_exceeded");
	return { content, details };
}

function projectBlock(
	block: ContentBlock,
	limits: GameMcpLimits,
	content: AgentToolResult<GameMcpToolDetails>["content"],
	kinds: string[],
): void {
	switch (block.type) {
		case "text":
			assertBytes(block.text, limits.maxResultTextBytes, "result_limit_exceeded");
			content.push({ type: "text", text: block.text });
			kinds.push("text");
			return;
		case "image":
			assertMedia(block.data, block.mimeType, "image/", limits);
			content.push({ type: "image", data: block.data, mimeType: block.mimeType });
			kinds.push("image");
			return;
		case "audio": {
			assertMedia(block.data, block.mimeType, "audio/", limits);
			const rendered = canonicalizeJsonValue({
				type: "audio",
				mimeType: block.mimeType,
				bytes: decodedBase64Bytes(block.data),
			});
			assertBytes(rendered, limits.maxResultTextBytes, "result_limit_exceeded");
			content.push({
				type: "text",
				text: rendered,
			});
			kinds.push("audio-metadata");
			return;
		}
		case "resource_link": {
			assertResourceFields(block.uri, block.mimeType, limits);
			const rendered = canonicalizeJsonValue({
				type: "resource_link",
				uri: block.uri,
				name: block.name,
				...(block.title === undefined ? {} : { title: block.title }),
				...(block.description === undefined ? {} : { description: block.description }),
				...(block.mimeType === undefined ? {} : { mimeType: block.mimeType }),
			});
			assertBytes(rendered, limits.maxResultTextBytes, "result_limit_exceeded");
			content.push({ type: "text", text: rendered });
			kinds.push("resource-link");
			return;
		}
		case "resource": {
			assertResourceFields(block.resource.uri, block.resource.mimeType, limits);
			if ("text" in block.resource) {
				assertBytes(block.resource.text, limits.maxResultTextBytes, "result_limit_exceeded");
				const rendered = `<mcp_resource uri="${escapeAttribute(block.resource.uri)}">\n${block.resource.text}\n</mcp_resource>`;
				assertBytes(rendered, limits.maxResultTextBytes, "result_limit_exceeded");
				content.push({ type: "text", text: rendered });
				kinds.push("resource-text");
				return;
			}
			const mimeType = block.resource.mimeType ?? "application/octet-stream";
			if (mimeType.toLowerCase().startsWith("image/")) {
				assertMedia(block.resource.blob, mimeType, "image/", limits);
				content.push({ type: "image", data: block.resource.blob, mimeType });
				kinds.push("resource-image");
				return;
			}
			assertMedia(block.resource.blob, mimeType, "", limits);
			const rendered = canonicalizeJsonValue({
				type: "resource_blob",
				uri: block.resource.uri,
				mimeType,
				bytes: decodedBase64Bytes(block.resource.blob),
			});
			assertBytes(rendered, limits.maxResultTextBytes, "result_limit_exceeded");
			content.push({ type: "text", text: rendered });
			kinds.push("resource-blob-metadata");
			return;
		}
	}
}

function assertMedia(data: string, mimeType: string, requiredPrefix: string, limits: GameMcpLimits): void {
	assertBytes(mimeType, limits.maxResultMimeTypeBytes, "result_limit_exceeded");
	if (requiredPrefix !== "" && !mimeType.toLowerCase().startsWith(requiredPrefix)) {
		throw new GameMcpError("unsupported_result_content");
	}
	if (decodedBase64Bytes(data) > limits.maxResultMediaBytes) throw new GameMcpError("result_limit_exceeded");
}

function decodedBase64Bytes(data: string): number {
	return Buffer.from(data, "base64").byteLength;
}

function assertResourceFields(uri: string, mimeType: string | undefined, limits: GameMcpLimits): void {
	assertBytes(uri, limits.maxResultUriBytes, "result_limit_exceeded");
	if (mimeType !== undefined) assertBytes(mimeType, limits.maxResultMimeTypeBytes, "result_limit_exceeded");
}

function searchText(summary: GameMcpToolSummary): string {
	return [summary.serverId, summary.serverDescription, summary.name, summary.title ?? "", summary.description ?? ""]
		.join("\n")
		.toLowerCase();
}

function uniqueServers(entries: readonly SelectedGameMcpTool[]): readonly GameMcpToolSummary[] {
	const seen = new Set<string>();
	return entries.flatMap((entry) => {
		if (seen.has(entry.tool.summary.serverId)) return [];
		seen.add(entry.tool.summary.serverId);
		return [entry.tool.summary];
	});
}

function selectedRevision(entries: readonly SelectedGameMcpTool[]): string {
	return sha256(
		canonicalizeJsonValue(
			entries.map((entry) => ({
				fingerprint: entry.tool.fingerprint,
				selection: entry.selection,
				connectionGeneration: entry.tool.connectionState.generation.toString(),
				catalogEpoch: entry.tool.catalogEpoch.toString(),
			})),
		),
	);
}

function prepareOptions(options: GameMcpRequestSourceOptions) {
	if (typeof options !== "object" || options === null || Array.isArray(options)) {
		throw new GameMcpError("invalid_configuration");
	}
	const sourceId = options.sourceId;
	const inputServers = options.servers;
	const inputLimits = options.limits;
	const inputLazyToolNames = options.lazyToolNames;
	const select = options.select;
	const authorizeCall = options.authorizeCall;
	const onDiagnostic = options.onDiagnostic;
	try {
		assertOpaqueIdentifier(sourceId);
	} catch {
		throw new GameMcpError("invalid_configuration");
	}
	if (!Array.isArray(inputServers) || inputServers.length === 0) {
		throw new GameMcpError("invalid_configuration");
	}
	const serverInputs = [...inputServers];
	if (typeof inputLimits !== "object" || inputLimits === null || Array.isArray(inputLimits)) {
		throw new GameMcpError("invalid_configuration");
	}
	const mutableLimits = {} as Record<keyof GameMcpLimits, number>;
	for (const field of MCP_LIMIT_NAMES) {
		const value = inputLimits[field];
		if (!Number.isSafeInteger(value) || value <= 0) throw new GameMcpError("invalid_configuration");
		mutableLimits[field] = value;
	}
	const limits = Object.freeze(mutableLimits);
	if (
		limits.requestIdleTimeoutMs > 2_147_483_647 ||
		limits.requestTotalTimeoutMs > 2_147_483_647 ||
		limits.requestTotalTimeoutMs < limits.requestIdleTimeoutMs ||
		serverInputs.length > limits.maxServers
	) {
		throw new GameMcpError("invalid_configuration");
	}
	const servers: GameMcpRequestSourceOptions["servers"][number][] = [];
	const serverIds = new Set<string>();
	for (const inputServer of serverInputs) {
		if (typeof inputServer !== "object" || inputServer === null || Array.isArray(inputServer)) {
			throw new GameMcpError("invalid_configuration");
		}
		const serverId = inputServer.serverId;
		const description = inputServer.description;
		const requirement = inputServer.requirement;
		const createTransport = inputServer.createTransport;
		if (
			typeof serverId !== "string" ||
			typeof description !== "string" ||
			(requirement !== "required" && requirement !== "optional") ||
			typeof createTransport !== "function" ||
			serverIds.has(serverId)
		) {
			throw new GameMcpError("invalid_configuration");
		}
		try {
			assertOpaqueIdentifier(serverId);
		} catch {
			throw new GameMcpError("invalid_configuration");
		}
		assertBytes(serverId, limits.maxServerIdBytes, "invalid_configuration");
		assertBytes(description, limits.maxServerDescriptionBytes, "invalid_configuration");
		serverIds.add(serverId);
		servers.push(Object.freeze({ serverId, description, requirement, createTransport }));
	}
	if (typeof inputLazyToolNames !== "object" || inputLazyToolNames === null || Array.isArray(inputLazyToolNames)) {
		throw new GameMcpError("invalid_configuration");
	}
	const lazyToolNames = Object.freeze({
		search: inputLazyToolNames.search,
		describe: inputLazyToolNames.describe,
		callParallel: inputLazyToolNames.callParallel,
		callSequential: inputLazyToolNames.callSequential,
	});
	const lazyNames = Object.values(lazyToolNames);
	if (
		new Set(lazyNames).size !== lazyNames.length ||
		lazyNames.some(
			(name) =>
				typeof name !== "string" ||
				!validateToolName(name).isValid ||
				encoder.encode(name).byteLength > limits.maxToolNameBytes,
		) ||
		typeof select !== "function" ||
		typeof authorizeCall !== "function" ||
		(onDiagnostic !== undefined && typeof onDiagnostic !== "function")
	) {
		throw new GameMcpError("invalid_configuration");
	}
	return Object.freeze({
		sourceId,
		servers: Object.freeze(servers),
		limits,
		lazyToolNames,
		select,
		authorizeCall,
		...(onDiagnostic === undefined ? {} : { onDiagnostic }),
	});
}

function assertBytes(
	value: string,
	maximum: number,
	code: "catalog_limit_exceeded" | "result_limit_exceeded" | "invalid_selection" | "invalid_configuration",
): void {
	if (encoder.encode(value).byteLength > maximum) throw new GameMcpError(code);
}

function assertCatalogBytes(tools: readonly LoadedGameMcpTool[], maximum: number): void {
	assertBytes(
		canonicalizeJsonValue(
			tools.map((tool) => ({
				summary: tool.summary,
				inputSchema: tool.inputSchema,
				...(tool.outputSchema === undefined ? {} : { outputSchema: tool.outputSchema }),
				fingerprint: tool.fingerprint,
			})),
		),
		maximum,
		"catalog_limit_exceeded",
	);
}

function mapMcpHealthFailure(error: unknown): RuntimeDependencyInspection {
	if (!(error instanceof GameMcpError)) throw error;
	switch (error.code) {
		case "authentication_required":
			return { state: "unavailable", reason: "authentication-required" };
		case "permission_denied":
			return { state: "unavailable", reason: "permission-denied" };
		case "server_timeout":
			return { state: "unavailable", reason: "timeout" };
		case "server_unavailable":
			return { state: "unavailable", reason: "transport" };
		case "server_capability_missing":
			return { state: "unavailable", reason: "capability-missing" };
		case "catalog_limit_exceeded":
		case "transport_limit_exceeded":
			return { state: "unavailable", reason: "capacity" };
		case "invalid_configuration":
			return { state: "unavailable", reason: "misconfigured" };
		case "transport_rejected":
			return { state: "unavailable", reason: "unknown" };
		case "catalog_incomplete":
		case "catalog_changed":
		case "invalid_tool_schema":
			return { state: "unavailable", reason: "dependency-failure" };
		default:
			throw error;
	}
}

function toolKey(serverId: string, name: string): string {
	return `${serverId.length}:${serverId}${name}`;
}

function deepFreezeJson(value: JsonValue): void {
	if (typeof value !== "object" || value === null || Object.isFrozen(value)) return;
	Object.freeze(value);
	for (const child of Array.isArray(value) ? value : Object.values(value)) deepFreezeJson(child);
}

function sha256(value: string): string {
	return createHash("sha256").update(value).digest("hex");
}

function escapeAttribute(value: string): string {
	return value.replaceAll("&", "&amp;").replaceAll('"', "&quot;").replaceAll("<", "&lt;");
}
