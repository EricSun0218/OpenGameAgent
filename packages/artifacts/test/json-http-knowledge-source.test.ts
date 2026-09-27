import {
	type JsonHttpKnowledgeLimits,
	JsonHttpKnowledgeSource,
	KnowledgeHttpError,
	type KnowledgeSourceRequest,
} from "@opengameagent/artifacts";
import { describe, expect, it, vi } from "vitest";

const limits: JsonHttpKnowledgeLimits = {
	maxEndpointBytes: 512,
	maxRequestBytes: 2048,
	maxResponseBytes: 4096,
	maxJsonDepth: 16,
	maxHeaders: 8,
	maxHeaderNameBytes: 64,
	maxHeaderValueBytes: 512,
	requestTimeoutMs: 1000,
};

const request: KnowledgeSourceRequest = {
	input: {
		sessionId: "session",
		actorId: "actor",
		inputId: "input",
		payload: { secret: "must-not-cross-transport", zone: "north" },
	},
	coordinate: { sessionId: "session", actorId: "actor", runId: "run", turn: "1" },
	query: { topic: "gate" },
	maxItems: 2,
};

describe("JsonHttpKnowledgeSource", () => {
	it("posts only the explicit query contract to one fixed endpoint", async () => {
		let receivedUrl: string | undefined;
		let receivedInit: RequestInit | undefined;
		const fetchImplementation = mockFetch(async (url, init) => {
			receivedUrl = url.toString();
			receivedInit = init;
			return jsonResponse({
				items: [{ itemId: "lore-1", title: "Gate", content: { state: "sealed" } }],
			});
		});
		const source = createSource({
			fetch: fetchImplementation,
			headers: async () => ({ Authorization: "Bearer host-token", "x-world": "one" }),
		});

		await expect(source.query(request, signal())).resolves.toEqual([
			{ itemId: "lore-1", title: "Gate", content: { state: "sealed" } },
		]);
		expect(receivedUrl).toBe("http://127.0.0.1:9123/knowledge");
		expect(receivedInit).toMatchObject({ method: "POST", redirect: "manual", credentials: "omit" });
		expect(JSON.parse(String(receivedInit?.body))).toEqual({ query: { topic: "gate" }, maxItems: 2 });
		expect(String(receivedInit?.body)).not.toContain("must-not-cross-transport");
		const headers = new Headers(receivedInit?.headers);
		expect(headers.get("content-type")).toBe("application/json");
		expect(headers.get("accept")).toBe("application/json");
		expect(headers.get("authorization")).toBe("Bearer host-token");
		expect(headers.get("x-world")).toBe("one");
	});

	it("adds only host-projected context and keeps header acquisition independent of model input", async () => {
		let body: unknown;
		const headers = vi.fn(async (_signal: AbortSignal) => {
			return Promise.resolve({ Authorization: "Bearer token" });
		});
		const projectContext = vi.fn(async (input: KnowledgeSourceRequest["input"]) => ({
			zone: (input.payload as { zone: string }).zone,
		}));
		const source = createSource({
			headers,
			projectContext,
			fetch: mockFetch(async (_url, init) => {
				body = JSON.parse(String(init?.body));
				return jsonResponse({ items: [] });
			}),
		});

		await source.query(request, signal());
		expect(headers.mock.calls[0]).toHaveLength(1);
		expect(projectContext).toHaveBeenCalledWith(request.input, expect.any(AbortSignal));
		expect(body).toEqual({ query: { topic: "gate" }, maxItems: 2, context: { zone: "north" } });
		expect(JSON.stringify(body)).not.toContain("must-not-cross-transport");
	});

	it("applies the request byte budget to the complete query and projected-context envelope", async () => {
		const fetchImplementation = vi.fn<typeof fetch>();
		const source = createSource({
			limits: { ...limits, maxRequestBytes: 128 },
			projectContext: async () => ({ text: "y".repeat(50) }),
			fetch: fetchImplementation,
		});
		await expect(source.query({ ...request, query: { text: "x".repeat(50) } }, signal())).rejects.toMatchObject({
			code: "request_too_large",
		});
		expect(fetchImplementation).not.toHaveBeenCalled();
	});

	it("accepts only HTTPS or exact loopback HTTP endpoints without credentials or fragments", () => {
		expect(() => createSource({ endpoint: "http://example.com/knowledge" })).toThrowError(
			expect.objectContaining({ code: "invalid_endpoint" }),
		);
		expect(() => createSource({ endpoint: "https://user:secret@example.com/knowledge" })).toThrowError(
			expect.objectContaining({ code: "invalid_endpoint" }),
		);
		expect(() => createSource({ endpoint: "https://example.com/knowledge#secret" })).toThrowError(
			expect.objectContaining({ code: "invalid_endpoint" }),
		);
		expect(() => createSource({ endpoint: "http://127.1.2.3:9123/knowledge" })).not.toThrow();
		expect(() => createSource({ endpoint: "http://[::1]:9123/knowledge" })).not.toThrow();
		expect(() => createSource({ endpoint: "https://example.com/knowledge" })).not.toThrow();
	});

	it("freezes mandatory budgets and rejects values below protocol floors or above Node's timer range", () => {
		const configured = { ...limits };
		const source = createSource({ limits: configured });
		configured.maxResponseBytes = 1;
		expect(source.limits.maxResponseBytes).toBe(limits.maxResponseBytes);
		for (const invalid of [
			{ ...limits, maxHeaders: 1 },
			{ ...limits, maxHeaderNameBytes: 3 },
			{ ...limits, maxHeaderValueBytes: 3 },
			{ ...limits, maxRequestBytes: 1 },
			{ ...limits, maxResponseBytes: 1 },
			{ ...limits, requestTimeoutMs: 2_147_483_648 },
		] as const) {
			expect(() => createSource({ limits: invalid })).toThrow(KnowledgeHttpError);
		}
	});

	it("rejects accessor-bearing and transport-controlled headers without invoking getters", async () => {
		let accessed = false;
		const accessor = Object.create(null) as Record<string, string>;
		Object.defineProperty(accessor, "Authorization", {
			enumerable: true,
			get() {
				accessed = true;
				return "secret";
			},
		});
		const withAccessor = createSource({ headers: async () => accessor });
		await expect(withAccessor.query(request, signal())).rejects.toMatchObject({ code: "invalid_headers" });
		expect(accessed).toBe(false);

		for (const name of ["host", "content-length", "content-type", "sec-fetch-site", "proxy-secret"]) {
			const source = createSource({ headers: async () => ({ [name]: "value" }) });
			await expect(source.query(request, signal())).rejects.toMatchObject({ code: "invalid_headers" });
		}
	});

	it("rejects redirects, non-success status, and non-UTF-8 JSON media types before parsing", async () => {
		const responses = [
			new Response(null, { status: 302, headers: { location: "https://other.example/" } }),
			new Response("private failure", { status: 503, headers: { "content-type": "application/json" } }),
			new Response("{}", { status: 200, headers: { "content-type": "text/plain" } }),
			new Response("{}", {
				status: 200,
				headers: { "content-type": "application/json; charset=iso-8859-1" },
			}),
		];
		const codes = ["redirect_blocked", "http_status", "unsupported_content_type", "unsupported_content_type"];
		for (const [index, response] of responses.entries()) {
			const source = createSource({ fetch: mockFetch(async () => response) });
			await expect(source.query(request, signal())).rejects.toMatchObject({ code: codes[index] });
		}
	});

	it("uses strict JSON and reads the structural response envelope", async () => {
		const invalidBodies = ['{"items":[],"items":[]}', '{"items":[{"unsafe":9007199254740992}]}', '{"items":[]}'];
		const contentTypes = ["application/json", "application/problem+json", "application/json"];
		const bodyOverrides = [undefined, undefined, new Uint8Array([0xff])];
		for (const [index, value] of invalidBodies.entries()) {
			const body = bodyOverrides[index] ?? value;
			const contentType = contentTypes[index];
			if (!contentType) throw new Error("Expected a content type fixture.");
			const source = createSource({
				fetch: mockFetch(async () => new Response(body, { status: 200, headers: { "content-type": contentType } })),
			});
			await expect(source.query(request, signal())).rejects.toMatchObject({ code: "invalid_response" });
		}
		const structural = createSource({
			fetch: mockFetch(
				async () =>
					new Response('{"items":[],"ignored":true}', {
						status: 200,
						headers: { "content-type": "application/json" },
					}),
			),
		});
		await expect(structural.query(request, signal())).resolves.toEqual([]);
	});

	it("enforces both declared and streamed response byte limits", async () => {
		const declared = createSource({
			limits: { ...limits, maxResponseBytes: 64 },
			fetch: mockFetch(
				async () =>
					new Response("", {
						status: 200,
						headers: { "content-type": "application/json", "content-length": "65" },
					}),
			),
		});
		await expect(declared.query(request, signal())).rejects.toMatchObject({ code: "response_too_large" });

		const stream = new ReadableStream<Uint8Array>({
			start(controller) {
				controller.enqueue(new TextEncoder().encode('{"items":['));
				controller.enqueue(new Uint8Array(64));
				controller.close();
			},
		});
		const streamed = createSource({
			limits: { ...limits, maxResponseBytes: 64 },
			fetch: mockFetch(
				async () => new Response(stream, { status: 200, headers: { "content-type": "application/json" } }),
			),
		});
		await expect(streamed.query(request, signal())).rejects.toMatchObject({ code: "response_too_large" });
	});

	it("stops waiting on source timeout or parent abort without exposing transport details", async () => {
		const unresolved = mockFetch(async () => await new Promise<Response>(() => undefined));
		const timed = createSource({ fetch: unresolved, limits: { ...limits, requestTimeoutMs: 5 } });
		await expect(timed.query(request, signal())).rejects.toMatchObject({
			name: "KnowledgeHttpError",
			code: "request_timeout",
			message: "External knowledge HTTP source failed.",
		});

		const controller = new AbortController();
		const pending = createSource({ fetch: unresolved }).query(request, controller.signal);
		await vi.waitFor(() => expect(unresolved).toHaveBeenCalledTimes(2));
		controller.abort(new Error("owning run aborted"));
		await expect(pending).rejects.toThrow("owning run aborted");
	});
});

function createSource(
	overrides: Partial<ConstructorParameters<typeof JsonHttpKnowledgeSource>[0]> = {},
): JsonHttpKnowledgeSource {
	return new JsonHttpKnowledgeSource({
		sourceId: "lore",
		description: "Authorized world lore.",
		endpoint: "http://127.0.0.1:9123/knowledge",
		limits,
		fetch: mockFetch(async () => jsonResponse({ items: [] })),
		...overrides,
	});
}

function jsonResponse(value: unknown): Response {
	return new Response(JSON.stringify(value), {
		status: 200,
		headers: { "content-type": "application/json; charset=utf-8" },
	});
}

function mockFetch(
	implementation: (
		url: Parameters<typeof globalThis.fetch>[0],
		init?: Parameters<typeof globalThis.fetch>[1],
	) => Promise<Response>,
): typeof globalThis.fetch {
	return vi.fn(implementation) as unknown as typeof globalThis.fetch;
}

function signal(): AbortSignal {
	return new AbortController().signal;
}
