import { createHash } from "node:crypto";
import type { GameInput, GameTool, JsonObject, JsonValue } from "@opengameagent/protocol";
import type { GameContextProvider, GameToolProvider } from "@opengameagent/runtime";
import type { GameMemory, GameMemoryQuery, GameMemoryScope, SqliteGameMemoryStore } from "./memory.js";

export interface GameMemoryExtensionOptions {
	store: SqliteGameMemoryStore;
	buildRecallQuery?: (
		input: GameInput,
		signal: AbortSignal,
	) => Promise<GameMemoryQuery | undefined> | GameMemoryQuery | undefined;
	contextName?: string;
	contextPriority?: number;
	allowedWriteScopes?: readonly GameMemoryScope[];
	authorizeWrite?: (input: GameInput, memory: GameMemory, signal: AbortSignal) => Promise<boolean> | boolean;
	includeRememberTool?: boolean;
	includeSearchTool?: boolean;
}

export interface GameMemoryExtensionResources {
	contextProvider?: GameContextProvider;
	toolProvider: GameToolProvider;
}

function asJson(value: unknown): JsonValue {
	return JSON.parse(JSON.stringify(value)) as JsonValue;
}

function canonical(value: JsonValue): string {
	if (value === null || typeof value !== "object") return JSON.stringify(value);
	if (Array.isArray(value)) return `[${value.map(canonical).join(",")}]`;
	return `{${Object.keys(value)
		.sort()
		.map((key) => `${JSON.stringify(key)}:${canonical(value[key] ?? null)}`)
		.join(",")}}`;
}

function stringArray(value: JsonValue | undefined, name: string, maximum: number): string[] | undefined {
	if (value === undefined) return undefined;
	if (!Array.isArray(value) || value.length > maximum || value.some((item) => typeof item !== "string")) {
		throw new TypeError(`${name} must be a bounded string array.`);
	}
	return value as string[];
}

function stableMemoryId(input: GameInput, callId: string, args: JsonObject): string {
	const hash = createHash("sha256")
		.update(
			canonical({
				world: input.session.worldId,
				save: input.session.saveId,
				timeline: input.session.timelineId,
				generation: input.session.generation,
				owner: input.session.ownerId,
				actor: input.session.actorId,
				input: input.id,
				call: callId,
				args,
			}),
		)
		.digest("base64url")
		.slice(0, 32);
	return `mem_${hash}`;
}

const anyJsonSchema: JsonObject = {
	anyOf: [
		{ type: "null" },
		{ type: "string" },
		{ type: "number" },
		{ type: "boolean" },
		{ type: "array", items: {}, maxItems: 256 },
		{ type: "object", additionalProperties: true },
	],
};

function rememberTool(input: GameInput, options: GameMemoryExtensionOptions): GameTool {
	return {
		definition: {
			name: "remember_game_memory",
			label: "Remember game memory",
			description: "Persist one bounded memory for this game actor, owner, or world.",
			parameters: {
				type: "object",
				properties: {
					scope: { type: "string", enum: ["actor", "owner", "world"] },
					kind: { type: "string", minLength: 1, maxLength: 192 },
					content: anyJsonSchema,
					searchText: { type: "string", maxLength: 16_384 },
					tags: { type: "array", items: { type: "string", minLength: 1, maxLength: 64 }, maxItems: 64 },
					importance: { type: "number", minimum: 0, maximum: 1 },
				},
				required: ["scope", "kind", "content", "importance"],
				additionalProperties: false,
			},
		},
		async execute(call, context) {
			const { signal } = context;
			const scope = call.arguments["scope"];
			const kind = call.arguments["kind"];
			const importance = call.arguments["importance"];
			if (scope !== "actor" && scope !== "owner" && scope !== "world") throw new TypeError("Memory scope is invalid.");
			if (!(options.allowedWriteScopes ?? ["actor"]).includes(scope))
				throw new Error("Memory scope is not authorized.");
			if (typeof kind !== "string" || typeof importance !== "number")
				throw new TypeError("Memory arguments are invalid.");
			const content = call.arguments["content"];
			if (content === undefined) throw new TypeError("Memory content is required.");
			const memory: GameMemory = {
				id: stableMemoryId(input, call.id, call.arguments),
				session: input.session,
				scope,
				kind,
				content,
				...(typeof call.arguments["searchText"] === "string" ? { searchText: call.arguments["searchText"] } : {}),
				...(call.arguments["tags"] === undefined
					? {}
					: { tags: stringArray(call.arguments["tags"], "tags", 64) ?? [] }),
				importance,
				moment: input.moment,
				createdAt: Math.max(0, Math.trunc(input.moment.tick)),
			};
			if ((await options.authorizeWrite?.(input, memory, signal)) === false)
				throw new Error("Memory write was rejected by the host.");
			await options.store.put(memory, signal);
			return { content: [{ type: "json", value: { id: memory.id, stored: true, scope: memory.scope } }] };
		},
	};
}

function searchTool(input: GameInput, options: GameMemoryExtensionOptions): GameTool {
	return {
		definition: {
			name: "search_game_memory",
			label: "Search game memory",
			description: "Recall bounded memories visible to this actor from the current save timeline.",
			parameters: {
				type: "object",
				properties: {
					text: { type: "string", maxLength: 16_384 },
					scopes: {
						type: "array",
						items: { type: "string", enum: ["actor", "owner", "world"] },
						maxItems: 3,
						uniqueItems: true,
					},
					kinds: { type: "array", items: { type: "string", minLength: 1, maxLength: 192 }, maxItems: 32 },
					tags: { type: "array", items: { type: "string", minLength: 1, maxLength: 64 }, maxItems: 32 },
					atOrBeforeTick: { type: "number" },
					minimumImportance: { type: "number", minimum: 0, maximum: 1 },
					limit: { type: "integer", minimum: 1, maximum: 32 },
				},
				required: ["limit"],
				additionalProperties: false,
			},
		},
		async execute(call, context) {
			const { signal } = context;
			const limit = call.arguments["limit"];
			if (typeof limit !== "number") throw new TypeError("Memory search limit is required.");
			const scopes = stringArray(call.arguments["scopes"], "scopes", 3) as GameMemoryScope[] | undefined;
			const result = await options.store.search(
				{
					session: input.session,
					...(typeof call.arguments["text"] === "string" ? { text: call.arguments["text"] } : {}),
					...(scopes === undefined ? {} : { scopes }),
					...(call.arguments["kinds"] === undefined
						? {}
						: { kinds: stringArray(call.arguments["kinds"], "kinds", 32) ?? [] }),
					...(call.arguments["tags"] === undefined
						? {}
						: { tags: stringArray(call.arguments["tags"], "tags", 32) ?? [] }),
					...(typeof call.arguments["atOrBeforeTick"] === "number"
						? { atOrBeforeTick: call.arguments["atOrBeforeTick"] }
						: {}),
					...(typeof call.arguments["minimumImportance"] === "number"
						? { minimumImportance: call.arguments["minimumImportance"] }
						: {}),
					limit,
				},
				signal,
			);
			return {
				content: [
					{
						type: "json",
						value: asJson(result.matches.map((match) => ({ memory: match.memory, score: match.score }))),
					},
				],
				details: asJson({ diagnostics: result.diagnostics }),
			};
		},
	};
}

export function createGameMemoryExtension(options: GameMemoryExtensionOptions): GameMemoryExtensionResources {
	if (!options.store) throw new TypeError("A memory store is required.");
	const contextProvider: GameContextProvider | undefined = options.buildRecallQuery
		? {
				async provide(input, signal) {
					const query = await options.buildRecallQuery?.(input, signal);
					if (!query) return undefined;
					const result = await options.store.search({ ...query, session: input.session }, signal);
					return {
						name: options.contextName ?? "memory",
						priority: options.contextPriority ?? 50,
						value: asJson(result.matches.map((match) => ({ memory: match.memory, score: match.score }))),
					};
				},
			}
		: undefined;
	return {
		...(contextProvider === undefined ? {} : { contextProvider }),
		toolProvider: {
			async provide(input) {
				return [
					...(options.includeRememberTool === false ? [] : [rememberTool(input, options)]),
					...(options.includeSearchTool === false ? [] : [searchTool(input, options)]),
				];
			},
		},
	};
}
