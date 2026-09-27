import type { AgentTool, AgentToolResult } from "@earendil-works/pi-agent-core";
import type { GameToolResultTransformContext, NormalizedGameInput } from "@opengameagent/agent";
import {
	ArtifactCapacityError,
	ArtifactConflictError,
	ArtifactCorruptionError,
	type ArtifactLimits,
	type ArtifactPutResult,
	type ArtifactStore,
	ArtifactValidationError,
	digest,
	GameArtifactService,
	InMemoryArtifactStore,
	validateArtifactRecord,
} from "@opengameagent/artifacts";
import type { RunCoordinate } from "@opengameagent/protocol";
import { describe, expect, it, vi } from "vitest";

const limits: ArtifactLimits = {
	maxIdentifierBytes: 128,
	maxCounterDigits: 20,
	maxContentTypeBytes: 128,
	maxArtifactBytes: 4096,
	maxArtifactsPerStream: 4,
	maxStoredBytesPerStream: 8192,
	maxInlineToolResultBytes: 32,
	maxReadBytes: 128,
	maxModelResultBytes: 1024,
	maxPurgeBatch: 4,
};

const input = {
	sessionId: "session",
	actorId: "actor",
	inputId: "input",
	payload: { text: "inspect" },
} as const;

const coordinate = { sessionId: "session", actorId: "actor", runId: "run", turn: "1" } as const;

describe("GameArtifactService", () => {
	it("freezes mandatory limits and rejects configurations that cannot store an offloaded result", () => {
		const configured = { ...limits };
		const store = new InMemoryArtifactStore(configured);
		const service = new GameArtifactService({
			store,
			limits: configured,
			now: () => "2026-08-29T01:00:00.000Z",
		});
		expect(service.provenanceKind).toBe("artifact");
		configured.maxArtifactBytes = 99;
		expect(service.limits.maxArtifactBytes).toBe(limits.maxArtifactBytes);
		expect(
			() =>
				new InMemoryArtifactStore({
					...limits,
					maxArtifactBytes: limits.maxInlineToolResultBytes,
				}),
		).toThrow(ArtifactValidationError);
	});

	it("offloads an oversized all-text Pi Tool result and reads UTF-8 byte pages in the exact actor stream", async () => {
		const store = new InMemoryArtifactStore(limits);
		const service = createService(store);
		const text = "界🙂".repeat(20);
		const completed = { content: [{ type: "text" as const, text }], details: { retained: true } };
		const first = await projectResult(service, completed, "inspect_world", "call", { scope: "nearby" });
		const second = await projectResult(service, completed, "inspect_world", "call", { scope: "nearby" });
		const reference = textResult(first);
		const repeatedReference = textResult(second);
		expect(first.details).toEqual({ retained: true });
		expect(reference).toEqual(repeatedReference);
		expect(reference).toMatchObject({
			status: "stored",
			artifactId: expect.stringMatching(/^[0-9a-f]{64}$/u),
			contentType: "text/plain",
			totalBytes: 140,
			readTool: "read_game_artifact",
		});

		const read = requestTool(service, "read_game_artifact");
		const firstPage = textResult(await read.execute("read-1", { artifactId: reference.artifactId, maxBytes: 5 }));
		expect(firstPage).toMatchObject({
			status: "ok",
			offsetBytes: 0,
			content: "界",
			contentBytes: 3,
			nextOffsetBytes: 3,
			totalBytes: 140,
		});
		const secondPage = textResult(
			await read.execute("read-2", { artifactId: reference.artifactId, offsetBytes: 3, maxBytes: 4 }),
		);
		expect(secondPage).toMatchObject({ content: "🙂", contentBytes: 4, nextOffsetBytes: 7 });

		const otherInput = { ...input, actorId: "other" };
		expect(
			textResult(
				await requestTool(service, "read_game_artifact", otherInput, {
					...coordinate,
					actorId: "other",
				}).execute("read", { artifactId: reference.artifactId }),
			),
		).toEqual({ status: "not_found" });
	});

	it("returns a bounded non-retry marker when durable storage or artifact size cannot preserve a completed Tool output", async () => {
		const constrained = { ...limits, maxArtifactsPerStream: 1, maxStoredBytesPerStream: 4096 };
		const store = new InMemoryArtifactStore(constrained);
		const service = createService(store, constrained);
		const completed = { content: [{ type: "text" as const, text: "x".repeat(64) }], details: {} };
		expect(textResult(await projectResult(service, completed, "inspect", "first", {}))).toMatchObject({
			status: "stored",
		});
		expect(textResult(await projectResult(service, completed, "inspect", "second", {}))).toEqual({
			status: "output_unavailable",
			reason: "artifact_storage_failed",
			doNotRetry: true,
		});

		const oversized = {
			content: [{ type: "text" as const, text: "y".repeat(constrained.maxArtifactBytes + 1) }],
			details: {},
		};
		expect(
			textResult(
				await projectResult(
					service,
					oversized,
					"oversized",
					"call",
					{},
					{ ...input, inputId: "oversized" },
					{ ...coordinate, runId: "oversized-run" },
				),
			),
		).toEqual({
			status: "output_unavailable",
			reason: "artifact_too_large",
			doNotRetry: true,
		});
	});

	it("stops waiting when an uncooperative artifact backend ignores the owning run abort", async () => {
		const put = vi.fn(async () => await new Promise<ArtifactPutResult>(() => undefined));
		const store: ArtifactStore = {
			limits: Object.freeze({ ...limits }),
			put,
			async read() {
				return undefined;
			},
			async purge() {
				return 0;
			},
		};
		const service = new GameArtifactService({
			store,
			limits,
			now: () => "2026-08-29T01:00:00.000Z",
		});
		const controller = new AbortController();
		const pending = projectResult(
			service,
			{ content: [{ type: "text", text: "x".repeat(64) }], details: {} },
			"inspect",
			"call",
			{},
			input,
			coordinate,
			controller.signal,
		);
		await vi.waitFor(() => expect(put).toHaveBeenCalledTimes(1));
		controller.abort(new Error("run aborted"));
		await expect(pending).rejects.toThrow("run aborted");
	});

	it("enforces immutable request identity, partition capacity, and bounded explicit purge", async () => {
		const constrained = { ...limits, maxArtifactsPerStream: 1, maxStoredBytesPerStream: 4096, maxPurgeBatch: 1 };
		const store = new InMemoryArtifactStore(constrained);
		let now = "2026-08-29T01:00:00.000Z";
		const service = new GameArtifactService({ store, limits: constrained, now: () => now });
		const request = {
			input,
			coordinate,
			toolCallId: "call",
			toolName: "knowledge",
			purpose: "knowledge_result",
			requestFingerprint: digest({ query: "one" }),
			contentType: "application/json",
			content: '{"answer":1}',
		} as const;
		const first = await service.storeText(request);
		now = "2026-08-29T01:00:00.500Z";
		await expect(service.storeText(request)).resolves.toEqual(first);
		await expect(service.storeText({ ...request, content: '{"answer":2}' })).rejects.toBeInstanceOf(
			ArtifactConflictError,
		);
		now = "2026-08-29T01:00:01.000Z";
		await expect(
			service.storeText({
				...request,
				toolCallId: "other",
				requestFingerprint: digest({ query: "two" }),
			}),
		).rejects.toBeInstanceOf(ArtifactCapacityError);
		await expect(store.purge(input, "2026-08-29T01:00:01.000Z", 1)).resolves.toBe(1);
		await expect(store.read(input, first.artifactId)).resolves.toBeUndefined();
	});

	it("rejects unknown persisted fields before traversing their values", async () => {
		const service = createService(new InMemoryArtifactStore(limits));
		const record = await service.storeText({
			input,
			coordinate,
			toolCallId: "call",
			toolName: "inspect",
			purpose: "tool_result",
			requestFingerprint: digest({ request: true }),
			contentType: "text/plain",
			content: "bounded",
		});
		let accessed = false;
		const corrupted = { ...record } as Record<string, unknown>;
		Object.defineProperty(corrupted, "junk", {
			enumerable: true,
			get() {
				accessed = true;
				return ["private", "unbounded"];
			},
		});
		expect(() => validateArtifactRecord(corrupted, limits)).toThrow(ArtifactCorruptionError);
		expect(accessed).toBe(false);
	});
});

function createService(store: InMemoryArtifactStore, configured: ArtifactLimits = limits): GameArtifactService {
	return new GameArtifactService({
		store,
		limits: configured,
		now: () => "2026-08-29T01:00:00.000Z",
	});
}

async function projectResult(
	service: GameArtifactService,
	result: AgentToolResult<unknown>,
	toolName: string,
	toolCallId: string,
	argumentsValue: GameToolResultTransformContext["arguments"],
	boundInput: Readonly<NormalizedGameInput> = input,
	boundCoordinate: Readonly<RunCoordinate> = coordinate,
	requestSignal = signal(),
): Promise<AgentToolResult<unknown>> {
	const contribution = service.collect(boundInput, requestSignal, boundCoordinate);
	const transformer = contribution.transformToolResult;
	if (!transformer) throw new Error("Expected the Artifact result transformer.");
	const context: GameToolResultTransformContext = {
		input: boundInput,
		coordinate: boundCoordinate,
		toolCallId,
		toolName,
		arguments: argumentsValue,
		descriptorFingerprint: "fingerprint",
		catalogRevision: "catalog",
		policyRevision: "policy",
		isError: false,
	};
	const content = await transformer(result, context, requestSignal);
	return content === undefined ? result : { ...result, content };
}

function requestTool(
	service: GameArtifactService,
	name: string,
	boundInput: Readonly<NormalizedGameInput> = input,
	boundCoordinate: Readonly<RunCoordinate> = coordinate,
): AgentTool {
	return requiredTool(service.collect(boundInput, signal(), boundCoordinate).tools ?? [], name);
}

function requiredTool(tools: readonly AgentTool[], name: string): AgentTool {
	const tool = tools.find((candidate) => candidate.name === name);
	if (!tool) throw new Error(`Expected ${name}.`);
	return tool;
}

function textResult(result: AgentToolResult<unknown>): Record<string, unknown> {
	const content = result.content[0];
	if (content?.type !== "text") throw new Error("Expected a text Tool result.");
	return JSON.parse(content.text) as Record<string, unknown>;
}

function signal(): AbortSignal {
	return new AbortController().signal;
}
