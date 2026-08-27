import { createHash } from "node:crypto";
import type { GameInput, GameSessionKey, GameTool, JsonObject, JsonValue } from "@opengameagent/protocol";
import type { GameToolProvider } from "@opengameagent/runtime";
import type { GameArtifactStore } from "./artifacts.js";

export interface GameExternalKnowledgeRequest {
	input: GameInput;
	query: JsonValue;
	limit: number;
}

export interface GameExternalKnowledgeItem {
	id: string;
	title: string;
	payload: JsonValue;
	summary?: string;
	uri?: string;
	metadata?: Readonly<Record<string, string>>;
}

export interface GameExternalKnowledgeSource {
	id: string;
	query(request: GameExternalKnowledgeRequest, signal: AbortSignal): Promise<readonly GameExternalKnowledgeItem[]>;
}

export interface GameExternalKnowledgeOptions {
	sources: readonly GameExternalKnowledgeSource[];
	artifactStore?: GameArtifactStore;
	maximumInlineCharacters?: number;
	maximumResultCharacters?: number;
	defaultLimit?: number;
}

function boundedText(value: unknown, name: string, maximum: number): string {
	if (typeof value !== "string" || !value || value.length > maximum) throw new TypeError(`${name} is invalid.`);
	return value;
}

function normalizeMetadata(value: unknown): Readonly<Record<string, string>> | undefined {
	if (value === undefined) return undefined;
	if (value === null || Array.isArray(value) || typeof value !== "object")
		throw new TypeError("Knowledge metadata is invalid.");
	const entries = Object.entries(value);
	if (entries.length > 128) throw new RangeError("Knowledge metadata has too many fields.");
	for (const [key, item] of entries) {
		boundedText(key, "Knowledge metadata key", 256);
		if (typeof item !== "string" || item.length > 16_384) throw new TypeError("Knowledge metadata value is invalid.");
	}
	return Object.fromEntries(entries.sort(([left], [right]) => left.localeCompare(right))) as Record<string, string>;
}

function normalizeItem(value: GameExternalKnowledgeItem): GameExternalKnowledgeItem {
	const serialized = JSON.stringify(value.payload);
	if (serialized.length > 10_000_000) throw new RangeError("Knowledge item payload is too large.");
	if (value.summary !== undefined && value.summary.length > 65_536)
		throw new RangeError("Knowledge item summary is too large.");
	if (value.uri !== undefined) {
		if (value.uri.length > 16_384) throw new RangeError("Knowledge item URI is too large.");
		const uri = new URL(value.uri);
		if (uri.username || uri.password) throw new TypeError("Knowledge item URI cannot contain credentials.");
	}
	return {
		id: boundedText(value.id, "Knowledge item id", 512),
		title: boundedText(value.title, "Knowledge item title", 4096),
		payload: structuredClone(value.payload),
		...(value.summary === undefined ? {} : { summary: value.summary }),
		...(value.uri === undefined ? {} : { uri: value.uri }),
		...(value.metadata === undefined
			? {}
			: { metadata: normalizeMetadata(value.metadata) as Readonly<Record<string, string>> }),
	};
}

function requiredInteger(argumentsValue: JsonObject, name: string, fallback: number): number {
	const value = argumentsValue[name] ?? fallback;
	if (typeof value !== "number" || !Number.isInteger(value)) throw new TypeError(`${name} must be an integer.`);
	return value;
}

function sameSession(left: GameSessionKey, right: GameSessionKey): boolean {
	return (
		left.worldId === right.worldId &&
		left.saveId === right.saveId &&
		left.timelineId === right.timelineId &&
		left.generation === right.generation &&
		left.ownerId === right.ownerId &&
		left.sessionId === right.sessionId &&
		left.actorId === right.actorId
	);
}

function artifactId(input: GameInput, sourceId: string, query: JsonValue, result: string): string {
	const hash = createHash("sha256");
	for (const value of [
		"oga-knowledge-v1",
		input.session.worldId,
		input.session.saveId,
		input.session.timelineId,
		String(input.session.generation),
		input.session.ownerId,
		input.session.sessionId,
		input.session.actorId,
		input.id,
		sourceId,
		JSON.stringify(query),
		result,
	])
		hash.update(value).update("\0");
	return `knowledge-v1-${hash.digest("hex")}`;
}

export function createExternalKnowledgeToolProvider(options: GameExternalKnowledgeOptions): GameToolProvider {
	const sources = new Map<string, GameExternalKnowledgeSource>();
	if (options.sources.length < 1 || options.sources.length > 128)
		throw new RangeError("One to 128 knowledge sources are required.");
	for (const source of options.sources) {
		const id = boundedText(source.id, "Knowledge source id", 256);
		if (sources.has(id)) throw new Error(`Duplicate knowledge source '${id}'.`);
		sources.set(id, source);
	}
	const maximumInlineCharacters = options.maximumInlineCharacters ?? 64 * 1024;
	const maximumResultCharacters = options.maximumResultCharacters ?? 10_000_000;
	const defaultLimit = options.defaultLimit ?? 8;
	if (
		!Number.isInteger(maximumInlineCharacters) ||
		maximumInlineCharacters < 1024 ||
		!Number.isInteger(maximumResultCharacters) ||
		maximumResultCharacters < maximumInlineCharacters ||
		maximumResultCharacters > 100_000_000 ||
		!Number.isInteger(defaultLimit) ||
		defaultLimit < 1 ||
		defaultLimit > 64
	)
		throw new RangeError("Knowledge result limits are invalid.");
	const sourceIds = [...sources.keys()].sort((left, right) => left.localeCompare(right));

	return {
		async provide(input): Promise<readonly GameTool[]> {
			return [
				{
					definition: {
						name: "query_external_knowledge",
						label: "Query external game knowledge",
						description:
							"Query one host-configured local or remote knowledge source. The source endpoint is never model-selected.",
						parameters: {
							type: "object",
							properties: {
								source: { type: "string", enum: sourceIds },
								query: {},
								limit: { type: "integer", minimum: 1, maximum: 64 },
							},
							required: ["source", "query"],
							additionalProperties: false,
						},
						risk: "read",
					},
					async execute(call, context) {
						if (!sameSession(input.session, context.input.session))
							return { isError: true, content: [{ type: "json", value: { error: "source_not_configured" } }] };
						const sourceId = call.arguments["source"];
						if (typeof sourceId !== "string") throw new TypeError("source must be a string.");
						const source = sources.get(sourceId);
						if (!source)
							return { isError: true, content: [{ type: "json", value: { error: "source_not_configured" } }] };
						const query = call.arguments["query"];
						if (query === undefined) throw new TypeError("query is required.");
						const limit = requiredInteger(call.arguments, "limit", defaultLimit);
						if (limit < 1 || limit > 64) throw new RangeError("limit must be 1..64.");
						const rawItems = await source.query({ input: context.input, query, limit }, context.signal);
						if (rawItems.length > limit) throw new Error("Knowledge source returned too many items.");
						const items = rawItems.map(normalizeItem);
						if (new Set(items.map((item) => item.id)).size !== items.length)
							throw new Error("Knowledge source returned duplicate item ids.");
						const serialized = JSON.stringify({ source: sourceId, items });
						if (serialized.length > maximumResultCharacters)
							throw new Error("Knowledge result exceeded its configured limit.");
						if (serialized.length <= maximumInlineCharacters)
							return { content: [{ type: "json", value: JSON.parse(serialized) as JsonValue }] };
						if (!options.artifactStore)
							return { isError: true, content: [{ type: "json", value: { error: "knowledge_result_too_large" } }] };
						const id = artifactId(context.input, sourceId, query, serialized);
						await options.artifactStore.put(
							{
								id,
								session: context.input.session,
								mediaType: "application/vnd.opengameagent.knowledge+json",
								content: serialized,
								moment: context.input.moment,
								metadata: { source: sourceId },
								createdAt: Date.now(),
							},
							context.signal,
						);
						return {
							content: [
								{
									type: "json",
									value: {
										artifactId: id,
										mediaType: "application/vnd.opengameagent.knowledge+json",
										totalCharacters: serialized.length,
										readTool: "read_agent_artifact",
									},
								},
							],
						};
					},
				},
			];
		},
	};
}

export interface JsonHttpGameKnowledgeSourceOptions {
	id: string;
	endpoint: string;
	headers?: (request: GameExternalKnowledgeRequest, signal: AbortSignal) => Promise<Readonly<Record<string, string>>>;
	maximumResponseBytes?: number;
	includeInputContext?: boolean;
	fetch?: typeof globalThis.fetch;
}

function validateEndpoint(endpoint: string): URL {
	const url = new URL(endpoint);
	if (url.username || url.password || url.hash)
		throw new TypeError("Knowledge endpoint contains forbidden URL components.");
	const local = url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]";
	if (url.protocol !== "https:" && !(local && url.protocol === "http:"))
		throw new TypeError("Knowledge endpoints must use HTTPS, except loopback HTTP.");
	return url;
}

export class JsonHttpGameKnowledgeSource implements GameExternalKnowledgeSource {
	readonly id: string;
	private readonly endpoint: URL;
	private readonly maximumResponseBytes: number;
	private readonly fetchImplementation: typeof globalThis.fetch;

	constructor(private readonly options: JsonHttpGameKnowledgeSourceOptions) {
		this.id = boundedText(options.id, "Knowledge source id", 256);
		this.endpoint = validateEndpoint(options.endpoint);
		this.maximumResponseBytes = options.maximumResponseBytes ?? 4_000_000;
		if (
			!Number.isInteger(this.maximumResponseBytes) ||
			this.maximumResponseBytes < 1024 ||
			this.maximumResponseBytes > 100_000_000
		)
			throw new RangeError("maximumResponseBytes is invalid.");
		this.fetchImplementation = options.fetch ?? globalThis.fetch;
	}

	async query(
		request: GameExternalKnowledgeRequest,
		signal: AbortSignal,
	): Promise<readonly GameExternalKnowledgeItem[]> {
		signal.throwIfAborted();
		const extraHeaders = (await this.options.headers?.(request, signal)) ?? {};
		const entries = Object.entries(extraHeaders);
		if (entries.length > 64) throw new Error("Knowledge authentication returned too many headers.");
		const headers = new Headers({ accept: "application/json", "content-type": "application/json" });
		for (const [name, value] of entries) {
			if (!name || name.length > 256 || /[\r\n\0]/u.test(name) || value.length > 65_536 || /[\r\n\0]/u.test(value))
				throw new Error("Knowledge authentication returned an invalid header.");
			headers.set(name, value);
		}
		const response = await this.fetchImplementation(this.endpoint, {
			method: "POST",
			headers,
			redirect: "error",
			signal,
			body: JSON.stringify({
				query: request.query,
				limit: request.limit,
				game: {
					type: request.input.type,
					moment: request.input.moment,
					...(this.options.includeInputContext ? { context: request.input.context ?? {} } : {}),
				},
			}),
		});
		if (!response.ok) throw new Error(`Knowledge endpoint failed with HTTP ${response.status}.`);
		const contentLength = Number(response.headers.get("content-length") ?? "0");
		if (Number.isFinite(contentLength) && contentLength > this.maximumResponseBytes)
			throw new Error("Knowledge response exceeded its configured byte limit.");
		const bytes = new Uint8Array(await response.arrayBuffer());
		if (bytes.byteLength > this.maximumResponseBytes)
			throw new Error("Knowledge response exceeded its configured byte limit.");
		let parsed: unknown;
		try {
			parsed = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
		} catch {
			throw new Error("Knowledge endpoint returned invalid JSON.");
		}
		const candidate = Array.isArray(parsed)
			? parsed
			: parsed !== null && typeof parsed === "object" && Array.isArray((parsed as { items?: unknown }).items)
				? (parsed as { items: unknown[] }).items
				: undefined;
		if (!candidate) throw new Error("Knowledge endpoint returned an invalid result envelope.");
		return candidate.map((item) => {
			if (item === null || Array.isArray(item) || typeof item !== "object")
				throw new Error("Knowledge endpoint returned an invalid item.");
			const record = item as Record<string, unknown>;
			return normalizeItem({
				id: record["id"] as string,
				title: record["title"] as string,
				payload: record["payload"] as JsonValue,
				...(record["summary"] === undefined ? {} : { summary: record["summary"] as string }),
				...(record["uri"] === undefined ? {} : { uri: record["uri"] as string }),
				...(record["metadata"] === undefined
					? {}
					: { metadata: record["metadata"] as Readonly<Record<string, string>> }),
			});
		});
	}
}
