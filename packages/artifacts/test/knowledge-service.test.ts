import type { AgentTool, AgentToolResult } from "@earendil-works/pi-agent-core";
import type { NormalizedGameInput } from "@opengameagent/agent";
import {
	GameKnowledgeService,
	type KnowledgeLimits,
	type KnowledgeSource,
	KnowledgeValidationError,
} from "@opengameagent/artifacts";
import { describe, expect, it, vi } from "vitest";

const knowledgeLimits: KnowledgeLimits = {
	maxIdentifierBytes: 128,
	maxCounterDigits: 20,
	maxSources: 4,
	maxSourceDescriptionBytes: 256,
	maxToolDescriptionBytes: 1024,
	maxQueryBytes: 1024,
	maxItemsPerQuery: 8,
	maxTitleBytes: 256,
	maxSummaryBytes: 512,
	maxUriBytes: 1024,
	maxMetadataEntries: 8,
	maxMetadataKeyBytes: 64,
	maxMetadataValueBytes: 256,
	maxItemBytes: 6144,
	maxResultBytes: 7168,
	maxJsonDepth: 16,
	maxConcurrentQueriesPerSource: 1,
	maxQueryDurationMs: 1000,
};

const input = {
	sessionId: "session",
	actorId: "actor",
	inputId: "input",
	payload: { command: "inspect", secret: "host-only" },
} as const;

const coordinate = { sessionId: "session", actorId: "actor", runId: "run", turn: "1" } as const;

describe("GameKnowledgeService", () => {
	it("freezes every host limit and rejects invalid derived budgets", () => {
		const configured = { ...knowledgeLimits };
		const service = createKnowledge(simpleSource(), configured);
		configured.maxItemsPerQuery = 99;
		expect(service.limits.maxItemsPerQuery).toBe(knowledgeLimits.maxItemsPerQuery);
		expect(() =>
			createKnowledge(simpleSource(), {
				...knowledgeLimits,
				maxQueryDurationMs: 2_147_483_648,
			}),
		).toThrow(KnowledgeValidationError);
		expect(() =>
			createKnowledge(simpleSource(), {
				...knowledgeLimits,
				maxResultBytes: knowledgeLimits.maxItemBytes - 1,
			}),
		).toThrow(KnowledgeValidationError);
	});

	it("exposes one direct parallel Pi Tool and passes detached, identity-bound requests", async () => {
		let received: unknown;
		const dangerousMetadata = JSON.parse(
			'{"__proto__":"literal-proto","constructor":"literal-constructor","region":"north"}',
		) as Record<string, string>;
		const returned = [
			{
				itemId: "lore-1",
				title: "Old gate",
				content: { fact: "sealed" },
				summary: "A sealed gate.",
				uri: "game://codex/lore-1",
				metadata: dangerousMetadata,
			},
		];
		const source: KnowledgeSource = {
			sourceId: "lore",
			description: "Authorized world lore.",
			async query(request) {
				received = request;
				return returned;
			},
		};
		const mutableInput = structuredClone(input);
		const service = createKnowledge(source);
		const tools = requestTools(service, mutableInput);
		(mutableInput.payload as { secret: string }).secret = "changed";
		const tool = requiredTool(tools, "query_external_knowledge");
		expect(tool.executionMode).toBe("parallel");
		expect(tools.map((candidate) => candidate.name)).toEqual(["query_external_knowledge"]);

		const result = await tool.execute("call", {
			sourceId: "lore",
			query: { topic: "gate" },
			maxItems: 2,
		});
		expect(received).toEqual({
			input,
			coordinate,
			query: { topic: "gate" },
			maxItems: 2,
		});
		expect(textResult(result)).toEqual({
			status: "ok",
			trust: "external_untrusted",
			sourceId: "lore",
			items: returned,
		});
		expect(result.details).toEqual({ knowledgeStatus: "ok", sourceId: "lore", itemCount: 1 });
		const normalizedItems = textResult(result).items as readonly { metadata: Record<string, string> }[];
		const normalizedMetadata = normalizedItems[0]?.metadata;
		if (!normalizedMetadata) throw new Error("Expected normalized metadata.");
		expect(Object.hasOwn(normalizedMetadata, "__proto__")).toBe(true);
		expect(Reflect.get(normalizedMetadata, "__proto__")).toBe("literal-proto");
		expect(normalizedMetadata.constructor).toBe("literal-constructor");
		const firstReturned = returned[0];
		if (!firstReturned) throw new Error("Expected a returned item.");
		firstReturned.content.fact = "mutated after completion";
		expect(textResult(result).items).toEqual([expect.objectContaining({ content: { fact: "sealed" } })]);
	});

	it("captures a dynamic source array and callback exactly once", async () => {
		let lengthReads = 0;
		let elementReads = 0;
		let queryReads = 0;
		const source = {
			sourceId: "stable",
			description: "Stable source.",
			get query() {
				queryReads += 1;
				return async () => [{ itemId: "one", title: "One", content: true }];
			},
		};
		const sources = new Proxy([source], {
			get(target, property, receiver) {
				if (property === "length") {
					lengthReads += 1;
					return lengthReads === 1 ? 1 : 2;
				}
				if (property === "0") elementReads += 1;
				return Reflect.get(target, property, receiver);
			},
		});
		const service = new GameKnowledgeService({
			sources: sources as readonly KnowledgeSource[],
			limits: knowledgeLimits,
		});
		expect(service.provenanceKind).toBe("knowledge");
		const result = await requiredTool(requestTools(service), "query_external_knowledge").execute("call", {
			sourceId: "stable",
			query: null,
			maxItems: 1,
		});
		expect(textResult(result)).toMatchObject({ status: "ok", sourceId: "stable" });
		expect({ lengthReads, elementReads, queryReads }).toEqual({ lengthReads: 1, elementReads: 1, queryReads: 1 });
	});

	it.each([-1, Number.NaN, 1.5])("rejects a dynamic source array with invalid captured length %s", (length) => {
		let elementReads = 0;
		const sources = new Proxy([simpleSource()], {
			get(target, property, receiver) {
				if (property === "length") return length;
				if (property === "0") elementReads += 1;
				return Reflect.get(target, property, receiver);
			},
		});
		expect(
			() => new GameKnowledgeService({ sources: sources as readonly KnowledgeSource[], limits: knowledgeLimits }),
		).toThrow(KnowledgeValidationError);
		expect(elementReads).toBe(0);
	});

	it("fails closed for accessor-bearing, duplicate, over-returned, and oversized source results", async () => {
		let accessed = false;
		const accessor = Object.create(null) as Record<string, unknown>;
		Object.defineProperty(accessor, "itemId", {
			enumerable: true,
			get() {
				accessed = true;
				return "secret";
			},
		});
		Object.defineProperties(accessor, {
			title: { enumerable: true, value: "Title" },
			content: { enumerable: true, value: { safe: true } },
		});
		const cases: readonly unknown[] = [
			[accessor],
			[
				{ itemId: "same", title: "One", content: null },
				{ itemId: "same", title: "Two", content: null },
			],
			[
				{ itemId: "one", title: "One", content: null },
				{ itemId: "two", title: "Two", content: null },
			],
			[{ itemId: "large", title: "Large", content: "x".repeat(knowledgeLimits.maxItemBytes) }],
		];
		for (const [index, value] of cases.entries()) {
			const service = createKnowledge({
				sourceId: `source-${index}`,
				description: "Invalid source result.",
				async query() {
					return value;
				},
			});
			const result = await requiredTool(requestTools(service), "query_external_knowledge").execute(`call-${index}`, {
				sourceId: `source-${index}`,
				query: null,
				maxItems: index === 2 ? 1 : 2,
			});
			expect(textResult(result)).toEqual({
				status: "knowledge_unavailable",
				reason: "invalid_source_result",
				doNotRetry: true,
			});
		}
		expect(accessed).toBe(false);
		let tailAccessed = false;
		const overReturned = [{ itemId: "one", title: "One", content: null }];
		Object.defineProperty(overReturned, 1, {
			enumerable: true,
			get() {
				tailAccessed = true;
				return { itemId: "two", title: "Two", content: null };
			},
		});
		const overReturnedService = createKnowledge({
			sourceId: "over-returned",
			description: "Over-returned source.",
			async query() {
				return overReturned;
			},
		});
		expect(
			textResult(
				await requiredTool(requestTools(overReturnedService), "query_external_knowledge").execute("call-tail", {
					sourceId: "over-returned",
					query: null,
					maxItems: 1,
				}),
			),
		).toMatchObject({ reason: "invalid_source_result" });
		expect(tailAccessed).toBe(false);
	});

	it("returns a complete normalized result and leaves generic offloading to the Artifact request source", async () => {
		const query = vi.fn(async () => [{ itemId: "large", title: "Large result", content: { text: "界".repeat(400) } }]);
		const service = createKnowledge({ sourceId: "archive", description: "Large archive.", query });
		const queryTool = requiredTool(requestTools(service), "query_external_knowledge");
		const first = await queryTool.execute("call", { sourceId: "archive", query: { key: "gate" }, maxItems: 1 });
		const second = await queryTool.execute("call", { sourceId: "archive", query: { key: "gate" }, maxItems: 1 });
		expect(textResult(first)).toEqual(textResult(second));
		expect(textResult(first)).toMatchObject({ status: "ok", trust: "external_untrusted", sourceId: "archive" });
		expect(first.details).toEqual({ knowledgeStatus: "ok", sourceId: "archive", itemCount: 1 });
		expect(query).toHaveBeenCalledTimes(2);
	});

	it("fails fast at per-source capacity and releases the slot when an uncooperative source is aborted", async () => {
		const query = vi
			.fn<KnowledgeSource["query"]>()
			.mockImplementationOnce(async () => await new Promise<unknown>(() => undefined))
			.mockResolvedValueOnce([{ itemId: "ready", title: "Ready", content: true }]);
		const service = createKnowledge({ sourceId: "slow", description: "Slow source.", query });
		const tool = requiredTool(requestTools(service), "query_external_knowledge");
		const controller = new AbortController();
		const pending = tool.execute("first", { sourceId: "slow", query: null, maxItems: 1 }, controller.signal);
		await vi.waitFor(() => expect(query).toHaveBeenCalledTimes(1));
		expect(textResult(await tool.execute("second", { sourceId: "slow", query: null, maxItems: 1 }))).toEqual({
			status: "knowledge_unavailable",
			reason: "capacity_exceeded",
			doNotRetry: true,
		});
		controller.abort(new Error("run aborted"));
		await expect(pending).rejects.toThrow("run aborted");
		expect(textResult(await tool.execute("third", { sourceId: "slow", query: null, maxItems: 1 }))).toMatchObject({
			status: "ok",
		});
	});

	it("bounds source timeout without retrying completed work", async () => {
		const timedQuery = vi
			.fn<KnowledgeSource["query"]>()
			.mockImplementationOnce(async () => await new Promise<unknown>(() => undefined))
			.mockResolvedValueOnce([{ itemId: "ready", title: "Ready", content: true }]);
		const timed = createKnowledge(
			{ sourceId: "timed", description: "Timed source.", query: timedQuery },
			{
				...knowledgeLimits,
				maxQueryDurationMs: 5,
			},
		);
		const timedTool = requiredTool(requestTools(timed), "query_external_knowledge");
		expect(textResult(await timedTool.execute("first", { sourceId: "timed", query: null, maxItems: 1 }))).toEqual({
			status: "knowledge_unavailable",
			reason: "source_timeout",
			doNotRetry: true,
		});
		expect(
			textResult(await timedTool.execute("second", { sourceId: "timed", query: null, maxItems: 1 })),
		).toMatchObject({
			status: "ok",
		});
	});

	it("does not expose source failures and distinguishes aggregate result overflow", async () => {
		const unavailableQuery = vi.fn(async () => {
			throw new Error("private source credential and stack");
		});
		const unavailable = createKnowledge({
			sourceId: "private",
			description: "Private failing source.",
			query: unavailableQuery,
		});
		const unavailableTool = requiredTool(requestTools(unavailable), "query_external_knowledge");
		expect(
			textResult(await unavailableTool.execute("call", { sourceId: "private", query: null, maxItems: 1 })),
		).toEqual({ status: "knowledge_unavailable", reason: "source_unavailable", doNotRetry: true });
		expect(unavailableQuery).toHaveBeenCalledTimes(1);

		const aggregateQuery = vi.fn(async () => [
			{ itemId: "one", title: "One", content: "x".repeat(600) },
			{ itemId: "two", title: "Two", content: "y".repeat(600) },
		]);
		const aggregate = createKnowledge(
			{ sourceId: "aggregate", description: "Aggregate source.", query: aggregateQuery },
			{ ...knowledgeLimits, maxItemBytes: 800, maxResultBytes: 1000 },
		);
		const aggregateTool = requiredTool(requestTools(aggregate), "query_external_knowledge");
		expect(
			textResult(await aggregateTool.execute("call", { sourceId: "aggregate", query: null, maxItems: 2 })),
		).toEqual({ status: "knowledge_unavailable", reason: "result_too_large", doNotRetry: true });
		expect(aggregateQuery).toHaveBeenCalledTimes(1);
	});

	it("publishes one request-bound Tool and leaves catalog collision ownership to GameAgent", () => {
		const service = createKnowledge(simpleSource());
		expect(requestTools(service).map((tool) => tool.name)).toEqual(["query_external_knowledge"]);
		expect(service.sourceId).toBe("knowledge");
	});
});

function createKnowledge(source: KnowledgeSource, limits: KnowledgeLimits = knowledgeLimits): GameKnowledgeService {
	return new GameKnowledgeService({ sources: [source], limits });
}

function simpleSource(): KnowledgeSource {
	return {
		sourceId: "lore",
		description: "Authorized lore.",
		async query() {
			return [{ itemId: "one", title: "One", content: null }];
		},
	};
}

function requiredTool(tools: readonly AgentTool[], name: string): AgentTool {
	const tool = tools.find((candidate) => candidate.name === name);
	if (!tool) throw new Error(`Expected ${name}.`);
	return tool;
}

function requestTools(
	service: GameKnowledgeService,
	boundInput: Readonly<NormalizedGameInput> = input,
): readonly AgentTool[] {
	return service.collect(boundInput, signal(), coordinate).tools ?? [];
}

function textResult(result: AgentToolResult<unknown>): Record<string, unknown> {
	const content = result.content[0];
	if (content?.type !== "text") throw new Error("Expected a text Tool result.");
	return JSON.parse(content.text) as Record<string, unknown>;
}

function signal(): AbortSignal {
	return new AbortController().signal;
}
