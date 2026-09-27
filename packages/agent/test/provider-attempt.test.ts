import type { AssistantMessage, Model } from "@earendil-works/pi-ai";
import { createAssistantMessageEventStream } from "@earendil-works/pi-ai";
import { describe, expect, it, vi } from "vitest";
import type { GameModelRequestProvenanceTemplate } from "../src/model-context-provenance.js";
import { createGameProvenanceAlias } from "../src/provenance-alias.js";
import { createProviderAttemptLifecycle } from "../src/provider-attempt.js";
import type { GameModelResponseProvenance } from "../src/types.js";

const MODEL: Model<"openai-responses"> = {
	id: "requested-model",
	name: "Requested model",
	api: "openai-responses",
	provider: "gateway",
	baseUrl: "https://example.invalid/v1",
	reasoning: false,
	input: ["text"],
	cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
	contextWindow: 10_000,
	maxTokens: 1_000,
};

describe("provider-attempt lifecycle", () => {
	it("settles a request without trusted pricing as explicitly unpriced", async () => {
		const responses: GameModelResponseProvenance[] = [];
		const lifecycle = lifecycleWithResponses(responses);
		const terminal = message();
		terminal.usage.cost = { input: 9, output: 8, cacheRead: 7, cacheWrite: 6, total: 30 };
		const attempt = lifecycle.begin(MODEL);
		await attempt.commit(() => messageStream(terminal));

		await attempt.settle(terminal);

		expect(responses).toMatchObject([{ cost: { kind: "unpriced" } }]);
		expect(responses[0]).not.toHaveProperty("cost.usd");
	});

	it("defaults successful usage to reported and uncaptured failed usage to unreported", async () => {
		const responses: GameModelResponseProvenance[] = [];
		const lifecycle = lifecycleWithResponses(responses);
		const succeeded = message();
		const failed = message({ stopReason: "error", errorMessage: "provider failure" });
		const cancelled = message({ stopReason: "aborted", errorMessage: "caller aborted" });

		for (const terminal of [succeeded, failed, cancelled]) {
			const attempt = lifecycle.begin(MODEL);
			await attempt.commit(() => messageStream(terminal));
			await attempt.settle(terminal);
		}

		expect(responses.map(({ usage }) => usage.kind)).toEqual(["reported", "unreported", "unreported"]);
	});

	it.each(["reported", "unreported"] as const)(
		"captures a final failed terminal with %s usage",
		async (usageObservation) => {
			const responses: GameModelResponseProvenance[] = [];
			const lifecycle = lifecycleWithResponses(responses);
			const terminal = message({ stopReason: "error", errorMessage: "provider failure" });
			const attempt = lifecycle.begin(MODEL);
			await attempt.commit(() => messageStream(terminal));

			attempt.captureTerminal(terminal, usageObservation);
			await attempt.settle(terminal);

			expect(responses).toMatchObject([{ usage: { kind: usageObservation } }]);
		},
	);

	it("settles only the exact captured terminal identity", async () => {
		const responses: GameModelResponseProvenance[] = [];
		const lifecycle = lifecycleWithResponses(responses);
		const terminal = message({ stopReason: "error", errorMessage: "provider failure" });
		const attempt = lifecycle.begin(MODEL);
		await attempt.commit(() => messageStream(terminal));
		attempt.captureTerminal(terminal, "reported");

		await expect(attempt.settle({ ...terminal })).rejects.toThrow("captured terminal identity");
		expect(responses).toHaveLength(0);

		await attempt.settle(terminal);
		expect(responses).toMatchObject([{ usage: { kind: "reported" } }]);
	});

	it("captures a failed terminal only on the committed current attempt", async () => {
		const lifecycle = lifecycleWithResponses([]);
		const terminal = message({ stopReason: "aborted", errorMessage: "caller aborted" });
		const attempt = lifecycle.begin(MODEL);

		expect(() => attempt.captureTerminal(terminal, "reported")).toThrow("committed provider attempt");
		await attempt.commit(() => messageStream(terminal));
		expect(() => attempt.captureTerminal(message(), "reported")).toThrow("error or aborted");
		attempt.captureTerminal(terminal, "reported");
		expect(() => attempt.captureTerminal(terminal, "reported")).toThrow("already captured");
		await attempt.settle(terminal);
		expect(() => attempt.captureTerminal(terminal, "reported")).toThrow("committed provider attempt");
	});

	it("settles Pi-catalog pricing with the exact terminal Pi cost", async () => {
		const responses: GameModelResponseProvenance[] = [];
		const lifecycle = lifecycleWithResponses(responses);
		const terminal = message();
		terminal.usage.cost = { input: 1, output: 2, cacheRead: 3, cacheWrite: 4, total: 10 };
		const attempt = lifecycle.begin(MODEL, { kind: "pi-catalog" });
		await attempt.commit(() => messageStream(terminal));

		await attempt.settle(terminal);

		expect(responses).toMatchObject([
			{
				cost: {
					kind: "calculated",
					source: "pi-catalog",
					usd: { input: 1, output: 2, cacheRead: 3, cacheWrite: 4, total: 10 },
				},
			},
		]);
		const response = responses[0];
		if (response?.cost.kind !== "calculated") throw new Error("Expected calculated cost provenance.");
		expect(Object.isFrozen(response.cost)).toBe(true);
		expect(Object.isFrozen(response.cost.usd)).toBe(true);
	});

	it("keeps host-rate-card and explicit-free settlements distinct", async () => {
		const responses: GameModelResponseProvenance[] = [];
		const lifecycle = lifecycleWithResponses(responses);
		const terminal = message();
		terminal.usage.cost = { input: 5, output: 6, cacheRead: 7, cacheWrite: 8, total: 26 };
		const hostRated = lifecycle.begin(MODEL, { kind: "host-rate-card" });
		await hostRated.commit(() => messageStream(terminal));
		await hostRated.settle(terminal);
		const explicitlyFree = lifecycle.begin(MODEL, { kind: "free" });
		await explicitlyFree.commit(() => messageStream(terminal));
		await explicitlyFree.settle(terminal);

		expect(responses).toMatchObject([
			{
				cost: {
					kind: "calculated",
					source: "host-rate-card",
					usd: { input: 5, output: 6, cacheRead: 7, cacheWrite: 8, total: 26 },
				},
			},
			{
				cost: {
					kind: "free",
					usd: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
				},
			},
		]);
	});

	it("captures the pricing basis before request persistence", async () => {
		const responses: GameModelResponseProvenance[] = [];
		const lifecycle = lifecycleWithResponses(responses);
		const pricing: { kind: "pi-catalog" | "unpriced" } = { kind: "pi-catalog" };
		const attempt = lifecycle.begin(MODEL, pricing);
		pricing.kind = "unpriced";
		const terminal = message();
		terminal.usage.cost = { input: 1, output: 0, cacheRead: 0, cacheWrite: 0, total: 1 };
		await attempt.commit(() => messageStream(terminal));

		await attempt.settle(terminal);

		expect(responses).toMatchObject([{ cost: { kind: "calculated", source: "pi-catalog" } }]);
	});

	it("does not consume an index for a discarded draft and settles committed attempts in order", async () => {
		const requests: string[] = [];
		const responses: string[] = [];
		const lifecycle = createProviderAttemptLifecycle({
			sink: {
				recordRequest: async (request) => {
					requests.push(request.attempt.index);
					return createGameProvenanceAlias("record", request.attempt.index);
				},
				recordResponse: async (recordId) => {
					responses.push(recordId);
				},
			},
			requestTemplate: requestTemplate(),
			now: () => 0,
			onCommit: () => undefined,
			onSettle: () => undefined,
		});

		const discarded = lifecycle.begin(MODEL);
		expect(discarded.index).toBe("1");
		discarded.discard();

		const first = lifecycle.begin(MODEL);
		expect(first.index).toBe("1");
		await first.commit(() => messageStream(message()));
		expect(() => lifecycle.begin(MODEL)).toThrow("open attempt");
		await first.settle(message());

		const second = lifecycle.begin({ ...MODEL, id: "fallback-model" });
		expect(second.index).toBe("2");
		await second.commit(() => messageStream(message({ model: "fallback-model" })));
		await second.settle(message({ model: "fallback-model" }));

		expect(requests).toEqual(["1", "2"]);
		expect(responses).toEqual([createGameProvenanceAlias("record", "1"), createGameProvenanceAlias("record", "2")]);
	});

	it("records the request before calling the provider and closes the commit/discard race", async () => {
		const order: string[] = [];
		let releaseRequest: ((recordId: ReturnType<typeof createGameProvenanceAlias>) => void) | undefined;
		const lifecycle = createProviderAttemptLifecycle({
			sink: {
				recordRequest: async () => {
					order.push("request");
					return await new Promise((resolve) => {
						releaseRequest = resolve;
					});
				},
				recordResponse: async () => undefined,
			},
			requestTemplate: requestTemplate(),
			now: () => 0,
			onCommit: () => undefined,
			onSettle: () => undefined,
		});
		const attempt = lifecycle.begin(MODEL);
		const start = vi.fn(() => {
			order.push("provider");
			return messageStream(message());
		});
		const committing = attempt.commit(start);

		expect(() => attempt.discard()).toThrow("draft");
		await expect(attempt.commit(start)).rejects.toThrow("draft");
		expect(start).not.toHaveBeenCalled();
		releaseRequest?.(createGameProvenanceAlias("record", "request"));
		await committing;
		expect(order).toEqual(["request", "provider"]);
		await attempt.settle(message());
	});

	it("does not classify a provider start failure after request persistence as a provenance failure", async () => {
		const providerFailure = new Error("provider start failed");
		const recordRequest = vi.fn(async () => createGameProvenanceAlias("record", "start-failure"));
		const recordResponse = vi.fn(async () => undefined);
		const onCommit = vi.fn();
		const onFailure = vi.fn();
		const lifecycle = createProviderAttemptLifecycle({
			sink: { recordRequest, recordResponse },
			requestTemplate: requestTemplate(),
			now: () => 0,
			onCommit,
			onSettle: () => undefined,
			onFailure,
		});
		const attempt = lifecycle.begin(MODEL);

		await expect(
			attempt.commit(() => {
				throw providerFailure;
			}),
		).rejects.toBe(providerFailure);

		expect({
			requestWrites: recordRequest.mock.calls.length,
			responseWrites: recordResponse.mock.calls.length,
			commits: onCommit.mock.calls.length,
			failures: onFailure.mock.calls.length,
		}).toEqual({ requestWrites: 1, responseWrites: 0, commits: 1, failures: 0 });
		expect(() => lifecycle.begin(MODEL)).toThrow("open attempt");
	});

	it("does not call the provider or allow fallback when request persistence fails", async () => {
		const start = vi.fn(() => messageStream(message()));
		const recordRequest = vi.fn(async () => {
			throw new Error("request store unavailable");
		});
		const onFailure = vi.fn();
		const lifecycle = createProviderAttemptLifecycle({
			sink: {
				recordRequest,
				recordResponse: async () => undefined,
			},
			requestTemplate: requestTemplate(),
			now: () => 0,
			onCommit: () => undefined,
			onSettle: () => undefined,
			onFailure,
		});
		const attempt = lifecycle.begin(MODEL);

		await expect(attempt.commit(start)).rejects.toThrow("request store unavailable");
		expect(start).not.toHaveBeenCalled();
		expect(onFailure).toHaveBeenCalledOnce();
		expect(() => lifecycle.begin(MODEL)).toThrow("lifecycle is closed");
		await expect(attempt.commit(start)).rejects.toThrow("draft");
		expect({ requests: recordRequest.mock.calls.length, failures: onFailure.mock.calls.length }).toEqual({
			requests: 1,
			failures: 1,
		});
	});

	it("permanently closes after a later attempt request cannot be persisted", async () => {
		let requestCount = 0;
		const recordRequest = vi.fn(async () => {
			requestCount += 1;
			if (requestCount === 2) throw new Error("later request store unavailable");
			return createGameProvenanceAlias("record", "first-request");
		});
		const recordResponse = vi.fn(async () => undefined);
		const onFailure = vi.fn();
		const firstStart = vi.fn(() => messageStream(message()));
		const laterStart = vi.fn(() => messageStream(message({ model: "fallback-model" })));
		const lifecycle = createProviderAttemptLifecycle({
			sink: { recordRequest, recordResponse },
			requestTemplate: requestTemplate(),
			now: () => 0,
			onCommit: () => undefined,
			onSettle: () => undefined,
			onFailure,
		});
		const first = lifecycle.begin(MODEL);
		await first.commit(firstStart);
		await first.settle(message());
		const later = lifecycle.begin({ ...MODEL, id: "fallback-model" });

		await expect(later.commit(laterStart)).rejects.toThrow("later request store unavailable");

		expect({
			requestWrites: recordRequest.mock.calls.length,
			responseWrites: recordResponse.mock.calls.length,
			firstStarts: firstStart.mock.calls.length,
			laterStarts: laterStart.mock.calls.length,
			failures: onFailure.mock.calls.length,
		}).toEqual({ requestWrites: 2, responseWrites: 1, firstStarts: 1, laterStarts: 0, failures: 1 });
		expect(() => lifecycle.begin(MODEL)).toThrow("lifecycle is closed");
	});

	it("closes permanently after response persistence fails but not after a settlement mismatch", async () => {
		const writtenResponses: GameModelResponseProvenance[] = [];
		const recordResponse = vi.fn(async (_recordId, response: GameModelResponseProvenance) => {
			writtenResponses.push(response);
			throw new Error("response store unavailable");
		});
		const onFailure = vi.fn();
		const lifecycle = createProviderAttemptLifecycle({
			sink: {
				recordRequest: async () => createGameProvenanceAlias("record", "request"),
				recordResponse,
			},
			requestTemplate: requestTemplate(),
			now: () => 0,
			onCommit: () => undefined,
			onSettle: () => undefined,
			onFailure,
		});
		const attempt = lifecycle.begin(MODEL);
		const terminal = message({ stopReason: "error", errorMessage: "provider failure" });
		await attempt.commit(() => messageStream(terminal));

		await expect(attempt.settle(message({ provider: "other" }))).rejects.toThrow("committed provider attempt");
		expect(recordResponse).not.toHaveBeenCalled();
		expect(onFailure).not.toHaveBeenCalled();
		attempt.captureTerminal(terminal, "reported");
		await expect(attempt.settle(terminal)).rejects.toThrow("response store unavailable");
		expect(onFailure).toHaveBeenCalledOnce();
		expect(writtenResponses).toMatchObject([{ usage: { kind: "reported" } }]);
		expect(() => lifecycle.begin(MODEL)).toThrow("lifecycle is closed");
		await expect(attempt.settle(terminal)).rejects.toThrow("committed provider attempt");
		expect({ responses: recordResponse.mock.calls.length, failures: onFailure.mock.calls.length }).toEqual({
			responses: 1,
			failures: 1,
		});
	});

	it("writes one response while settlement is pending", async () => {
		let releaseResponse: (() => void) | undefined;
		const recordResponse = vi.fn(async () => {
			await new Promise<void>((resolve) => {
				releaseResponse = resolve;
			});
		});
		const lifecycle = createProviderAttemptLifecycle({
			sink: {
				recordRequest: async () => createGameProvenanceAlias("record", "request"),
				recordResponse,
			},
			requestTemplate: requestTemplate(),
			now: () => 0,
			onCommit: () => undefined,
			onSettle: () => undefined,
		});
		const attempt = lifecycle.begin(MODEL);
		await attempt.commit(() => messageStream(message()));
		const settling = attempt.settle(message());

		await expect(attempt.settle(message())).rejects.toThrow("committed provider attempt");
		expect(recordResponse).toHaveBeenCalledOnce();
		releaseResponse?.();
		await settling;
	});
});

function lifecycleWithResponses(responses: GameModelResponseProvenance[]) {
	return createProviderAttemptLifecycle({
		sink: {
			recordRequest: async () => createGameProvenanceAlias("record", `pricing-${responses.length + 1}`),
			recordResponse: async (_recordId, response) => {
				responses.push(response);
			},
		},
		requestTemplate: requestTemplate(),
		now: () => 0,
		onCommit: () => undefined,
		onSettle: () => undefined,
	});
}

function requestTemplate(): GameModelRequestProvenanceTemplate {
	return {
		operation: "inference",
		coordinate: {
			scopeId: createGameProvenanceAlias("scope", "scope"),
			streamId: createGameProvenanceAlias("stream", "stream"),
			runId: createGameProvenanceAlias("run", "run"),
			turn: "1",
		},
		requestedRoute: {
			provider: createGameProvenanceAlias("provider", MODEL.provider),
			api: createGameProvenanceAlias("api", MODEL.api),
			model: createGameProvenanceAlias("model", MODEL.id),
		},
		modelContextFingerprint: "a".repeat(64),
		transcript: { revision: "1" },
		lineage: {
			baseInstructions: {
				sourceId: createGameProvenanceAlias("source", "agent"),
				referenceId: createGameProvenanceAlias("reference", "base"),
				revision: createGameProvenanceAlias("revision", "base"),
				contentFingerprint: "b".repeat(64),
			},
		},
		sources: [],
		tools: [],
		images: [],
	};
}

function message(overrides: Partial<AssistantMessage> = {}): AssistantMessage {
	return {
		role: "assistant",
		content: [],
		api: MODEL.api,
		provider: MODEL.provider,
		model: MODEL.id,
		usage: {
			input: 1,
			output: 1,
			cacheRead: 0,
			cacheWrite: 0,
			totalTokens: 2,
			cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
		},
		stopReason: "stop",
		timestamp: 1,
		...overrides,
	};
}

function messageStream(value: AssistantMessage) {
	const stream = createAssistantMessageEventStream();
	queueMicrotask(() => stream.push({ type: "done", reason: "stop", message: value }));
	return stream;
}
