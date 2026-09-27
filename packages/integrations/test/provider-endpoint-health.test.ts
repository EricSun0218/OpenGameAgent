import { createServer, type Server } from "node:http";
import { describe, expect, it, vi } from "vitest";
import { createProviderEndpointHealthDependency } from "../src/index.js";

const MAX_RESPONSE_BYTES = 4_096;

function fetchSequence(responses: readonly (Response | ((request: Request) => Response | Promise<Response>))[]): {
	readonly fetch: typeof fetch;
	readonly requests: Request[];
} {
	const requests: Request[] = [];
	let index = 0;
	return {
		requests,
		fetch: vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
			const request = new Request(input, init);
			requests.push(request);
			const candidate = responses[index++];
			if (candidate === undefined) throw new Error("Unexpected endpoint request.");
			return typeof candidate === "function" ? candidate(request) : candidate;
		}) as typeof fetch,
	};
}

function json(value: unknown, status = 200, headers: ConstructorParameters<typeof Headers>[0] = {}): Response {
	return new Response(JSON.stringify(value), {
		status,
		headers: { "content-type": "application/json", ...Object.fromEntries(new Headers(headers)) },
	});
}

describe("provider endpoint health", () => {
	it("uses the official OpenAI model catalog as availability evidence without inference or retry", async () => {
		const transport = fetchSequence([
			json({
				object: "list",
				data: [{ id: "local/game-model", object: "model", created: 0, owned_by: "host" }],
			}),
		]);
		const dependency = createProviderEndpointHealthDependency({
			id: "local-chat",
			kind: "local-endpoint",
			requirement: "required",
			protocol: "openai-compatible",
			baseUrl: "http://127.0.0.1:1234/v1",
			modelId: "local/game-model",
			maxResponseBytes: MAX_RESPONSE_BYTES,
			fetch: transport.fetch,
		});

		await expect(dependency.inspect?.(new AbortController().signal)).resolves.toEqual({ state: "available" });
		expect(transport.requests).toHaveLength(1);
		expect(transport.requests[0]?.method).toBe("GET");
		expect(new URL(transport.requests[0]?.url ?? "").pathname).toBe("/v1/models");
		expect(transport.requests[0]?.headers.has("authorization")).toBe(false);
	});

	it("reports a missing OpenAI-compatible model without probing generation", async () => {
		const transport = fetchSequence([
			json({ object: "list", data: [{ id: "another", object: "model", created: 0, owned_by: "host" }] }),
		]);
		const dependency = createProviderEndpointHealthDependency({
			id: "local-chat",
			kind: "local-endpoint",
			requirement: "optional",
			protocol: "openai-compatible",
			baseUrl: "https://models.example/v1",
			modelId: "expected",
			apiKey: "secret",
			maxResponseBytes: MAX_RESPONSE_BYTES,
			fetch: transport.fetch,
		});

		await expect(dependency.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "unavailable",
			reason: "capability-missing",
		});
		expect(transport.requests).toHaveLength(1);
		expect(transport.requests[0]?.headers.get("authorization")).toBe("Bearer secret");
	});

	it("accepts a bounded JSON catalog without requiring an exact content type", async () => {
		const transport = fetchSequence([
			new Response(JSON.stringify({ data: [{ id: "expected", vendor_extension: true }], ignored: "field" }), {
				headers: { "content-type": "text/plain; charset=utf-8" },
			}),
		]);
		const dependency = createProviderEndpointHealthDependency({
			id: "local-chat",
			kind: "local-endpoint",
			requirement: "optional",
			protocol: "openai-compatible",
			baseUrl: "https://models.example/v1",
			modelId: "expected",
			maxResponseBytes: MAX_RESPONSE_BYTES,
			fetch: transport.fetch,
		});

		await expect(dependency.inspect?.(new AbortController().signal)).resolves.toEqual({ state: "available" });
	});

	it("uses Ollama inventory and running-model APIs without loading or pulling", async () => {
		const running = fetchSequence([
			json({ models: [{ model: "qwen:latest" }] }),
			json({ models: [{ model: "qwen:latest" }] }),
		]);
		const ready = createProviderEndpointHealthDependency({
			id: "ollama-chat",
			kind: "local-endpoint",
			requirement: "required",
			protocol: "ollama",
			baseUrl: "http://localhost:11434",
			modelId: "qwen:latest",
			maxResponseBytes: MAX_RESPONSE_BYTES,
			fetch: running.fetch,
		});
		await expect(ready.inspect?.(new AbortController().signal)).resolves.toEqual({ state: "ready" });
		expect(running.requests.map((request) => `${request.method} ${new URL(request.url).pathname}`)).toEqual([
			"GET /api/tags",
			"GET /api/ps",
		]);

		const unloaded = fetchSequence([json({ models: [{ model: "qwen:latest" }] }), json({ models: [] })]);
		const available = createProviderEndpointHealthDependency({
			id: "ollama-chat",
			kind: "local-endpoint",
			requirement: "required",
			protocol: "ollama",
			baseUrl: "http://localhost:11434",
			modelId: "qwen:latest",
			maxResponseBytes: MAX_RESPONSE_BYTES,
			fetch: unloaded.fetch,
		});
		await expect(available.inspect?.(new AbortController().signal)).resolves.toEqual({ state: "available" });
	});

	it("stops Ollama inspection after an absent installed model", async () => {
		const transport = fetchSequence([json({ models: [{ model: "another" }] })]);
		const dependency = createProviderEndpointHealthDependency({
			id: "ollama-chat",
			kind: "local-endpoint",
			requirement: "required",
			protocol: "ollama",
			baseUrl: "http://localhost:11434",
			modelId: "expected",
			maxResponseBytes: MAX_RESPONSE_BYTES,
			fetch: transport.fetch,
		});

		await expect(dependency.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "unavailable",
			reason: "capability-missing",
		});
		expect(transport.requests).toHaveLength(1);
	});

	it.each([
		{ status: 200, body: { status: "ok" }, expected: { state: "ready" } },
		{
			status: 503,
			body: { error: { code: 503, message: "Loading model", type: "unavailable_error" } },
			expected: { state: "available" },
		},
	])("maps the documented llama.cpp health response for HTTP $status", async ({ status, body, expected }) => {
		const transport = fetchSequence([json(body, status)]);
		const dependency = createProviderEndpointHealthDependency({
			id: "llama-chat",
			kind: "local-endpoint",
			requirement: "required",
			protocol: "llama.cpp",
			healthUrl: "http://[::1]:8080/v1/health?model=game-model",
			maxResponseBytes: MAX_RESPONSE_BYTES,
			fetch: transport.fetch,
		});

		await expect(dependency.inspect?.(new AbortController().signal)).resolves.toEqual(expected);
		expect(transport.requests).toHaveLength(1);
		expect(transport.requests[0]?.url).toBe("http://[::1]:8080/v1/health?model=game-model");
	});

	it("does not treat an unrelated llama.cpp 503 as loading", async () => {
		const dependency = createProviderEndpointHealthDependency({
			id: "llama-chat",
			kind: "local-endpoint",
			requirement: "required",
			protocol: "llama.cpp",
			healthUrl: "http://127.0.0.1:8080/health",
			maxResponseBytes: MAX_RESPONSE_BYTES,
			fetch: fetchSequence([json({ error: { code: 503, type: "server_error" } }, 503)]).fetch,
		});
		await expect(dependency.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "unavailable",
			reason: "dependency-failure",
		});
	});

	it("requires the complete documented llama.cpp loading error shape", async () => {
		const dependency = createProviderEndpointHealthDependency({
			id: "llama-chat",
			kind: "local-endpoint",
			requirement: "required",
			protocol: "llama.cpp",
			healthUrl: "http://127.0.0.1:8080/health",
			maxResponseBytes: MAX_RESPONSE_BYTES,
			fetch: fetchSequence([
				json({ error: { code: 503, message: "Another unavailable condition", type: "unavailable_error" } }, 503),
			]).fetch,
		});
		await expect(dependency.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "unavailable",
			reason: "dependency-failure",
		});
	});

	it.each(["https://models.example/health/", "https://models.example/health?model=game-model/"])(
		"preserves the exact host-selected llama.cpp health URL %s",
		async (healthUrl) => {
			const transport = fetchSequence([json({ status: "ok" })]);
			const dependency = createProviderEndpointHealthDependency({
				id: "llama-chat",
				kind: "local-endpoint",
				requirement: "required",
				protocol: "llama.cpp",
				healthUrl,
				maxResponseBytes: MAX_RESPONSE_BYTES,
				fetch: transport.fetch,
			});

			await expect(dependency.inspect?.(new AbortController().signal)).resolves.toEqual({ state: "ready" });
			expect(transport.requests[0]?.url).toBe(healthUrl);
		},
	);

	it.each([
		[400, "misconfigured"],
		[401, "authentication-required"],
		[403, "permission-denied"],
		[404, "capability-missing"],
		[408, "timeout"],
		[429, "capacity"],
		[422, "misconfigured"],
		[500, "dependency-failure"],
	] as const)("maps HTTP %s to %s without exposing response text", async (status, reason) => {
		const transport = fetchSequence([json({ error: "private endpoint text" }, status)]);
		const dependency = createProviderEndpointHealthDependency({
			id: "endpoint",
			kind: "provider",
			requirement: "optional",
			protocol: "llama.cpp",
			healthUrl: "https://models.example/health",
			maxResponseBytes: MAX_RESPONSE_BYTES,
			fetch: transport.fetch,
		});
		await expect(dependency.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "unavailable",
			reason,
		});
	});

	it("classifies HTTP status before SSE metadata and cancels the failed body", async () => {
		let cancelled = false;
		const body = new ReadableStream<Uint8Array>({
			start(controller) {
				controller.enqueue(new TextEncoder().encode('{"error":"private"}'));
			},
			cancel() {
				cancelled = true;
			},
		});
		const dependency = createProviderEndpointHealthDependency({
			id: "endpoint",
			kind: "provider",
			requirement: "optional",
			protocol: "openai-compatible",
			baseUrl: "https://models.example/v1",
			modelId: "model",
			maxResponseBytes: MAX_RESPONSE_BYTES,
			fetch: fetchSequence([new Response(body, { status: 401, headers: { "content-type": "text/event-stream" } })])
				.fetch,
		});

		await expect(dependency.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "unavailable",
			reason: "authentication-required",
		});
		expect(cancelled).toBe(true);
	});

	it("classifies HTTP status before a declared response-body overflow", async () => {
		const dependency = createProviderEndpointHealthDependency({
			id: "endpoint",
			kind: "provider",
			requirement: "optional",
			protocol: "openai-compatible",
			baseUrl: "https://models.example/v1",
			modelId: "model",
			maxResponseBytes: MAX_RESPONSE_BYTES,
			fetch: fetchSequence([json({ error: "private" }, 403, { "content-length": String(MAX_RESPONSE_BYTES + 1) })])
				.fetch,
		});

		await expect(dependency.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "unavailable",
			reason: "permission-denied",
		});
	});

	it("maps malformed, redirected, oversized, and transport responses through the narrow health vocabulary", async () => {
		const redirected = json({ status: "ok" });
		Object.defineProperty(redirected, "redirected", { value: true });
		const cases = [
			{ response: new Response("not-json", { status: 200 }), reason: "dependency-failure" },
			{ response: redirected, reason: "misconfigured" },
			{
				response: json({ status: "ok" }, 200, { "content-length": String(MAX_RESPONSE_BYTES + 1) }),
				reason: "capacity",
			},
		] as const;
		for (const candidate of cases) {
			const dependency = createProviderEndpointHealthDependency({
				id: "endpoint",
				kind: "local-endpoint",
				requirement: "optional",
				protocol: "llama.cpp",
				healthUrl: "https://models.example/health",
				maxResponseBytes: MAX_RESPONSE_BYTES,
				fetch: fetchSequence([candidate.response]).fetch,
			});
			await expect(dependency.inspect?.(new AbortController().signal)).resolves.toEqual({
				state: "unavailable",
				reason: candidate.reason,
			});
		}

		const failedFetch = vi.fn(async () => {
			throw new Error("private socket address");
		}) as typeof fetch;
		const transportFailure = createProviderEndpointHealthDependency({
			id: "endpoint",
			kind: "local-endpoint",
			requirement: "optional",
			protocol: "llama.cpp",
			healthUrl: "https://models.example/health",
			maxResponseBytes: MAX_RESPONSE_BYTES,
			fetch: failedFetch,
		});
		await expect(transportFailure.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "unavailable",
			reason: "transport",
		});
	});

	it.each(["openai-compatible", "ollama"] as const)(
		"maps a %s JSON body read failure to dependency-failure after receiving the response",
		async (protocol) => {
			const body = new ReadableStream<Uint8Array>({
				start(controller) {
					controller.error(new TypeError("private truncated body"));
				},
			});
			const common = {
				id: "endpoint",
				kind: "local-endpoint" as const,
				requirement: "optional" as const,
				baseUrl: "https://models.example/v1",
				modelId: "model",
				maxResponseBytes: MAX_RESPONSE_BYTES,
				fetch: fetchSequence([new Response(body, { headers: { "content-type": "application/json" } })]).fetch,
			};
			const dependency = createProviderEndpointHealthDependency({ ...common, protocol });

			await expect(dependency.inspect?.(new AbortController().signal)).resolves.toEqual({
				state: "unavailable",
				reason: "dependency-failure",
			});
		},
	);

	it("maps a real native-fetch redirect to misconfigured without reaching its target", async () => {
		let targetRequests = 0;
		const target = await listen(
			createServer((_request, response) => {
				targetRequests += 1;
				response.end(JSON.stringify({ data: [{ id: "model" }] }));
			}),
		);
		const origin = await listen(
			createServer((_request, response) => {
				response.writeHead(307, { location: `${target.url}/v1/models` });
				response.end();
			}),
		);
		try {
			const dependency = createProviderEndpointHealthDependency({
				id: "endpoint",
				kind: "local-endpoint",
				requirement: "optional",
				protocol: "openai-compatible",
				baseUrl: `${origin.url}/v1`,
				modelId: "model",
				maxResponseBytes: MAX_RESPONSE_BYTES,
			});

			await expect(dependency.inspect?.(new AbortController().signal)).resolves.toEqual({
				state: "unavailable",
				reason: "misconfigured",
			});
			expect(targetRequests).toBe(0);
		} finally {
			await Promise.all([close(origin.server), close(target.server)]);
		}
	});

	it("maps streamed body overflow to capacity", async () => {
		const body = new ReadableStream<Uint8Array>({
			start(controller) {
				controller.enqueue(new TextEncoder().encode('{"status":"'));
				controller.enqueue(new TextEncoder().encode("x".repeat(64)));
				controller.close();
			},
		});
		const dependency = createProviderEndpointHealthDependency({
			id: "endpoint",
			kind: "local-endpoint",
			requirement: "optional",
			protocol: "llama.cpp",
			healthUrl: "https://models.example/health",
			maxResponseBytes: 16,
			fetch: fetchSequence([new Response(body, { headers: { "content-type": "application/json" } })]).fetch,
		});
		await expect(dependency.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "unavailable",
			reason: "capacity",
		});
	});

	it("rejects an SSE catalog before parsing it as one complete JSON response", async () => {
		let cancelled = false;
		const body = new ReadableStream<Uint8Array>({
			start(controller) {
				controller.enqueue(new TextEncoder().encode('data: {"data":[]}\n\n'));
			},
			cancel() {
				cancelled = true;
			},
		});
		const dependency = createProviderEndpointHealthDependency({
			id: "endpoint",
			kind: "local-endpoint",
			requirement: "optional",
			protocol: "openai-compatible",
			baseUrl: "https://models.example/v1",
			modelId: "model",
			maxResponseBytes: MAX_RESPONSE_BYTES,
			fetch: fetchSequence([new Response(body, { headers: { "content-type": "text/event-stream" } })]).fetch,
		});
		await expect(dependency.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "unavailable",
			reason: "dependency-failure",
		});
		expect(cancelled).toBe(true);
	});

	it("propagates caller cancellation and snapshots mutable configuration", async () => {
		const controller = new AbortController();
		const options = {
			id: "endpoint",
			kind: "local-endpoint" as const,
			requirement: "required" as const,
			protocol: "llama.cpp" as const,
			healthUrl: "https://models.example/health",
			maxResponseBytes: MAX_RESPONSE_BYTES,
			headers: { "x-host": "captured" },
			fetch: vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
				const request = new Request(input, init);
				expect(request.headers.get("x-host")).toBe("captured");
				await new Promise<void>((_resolve, reject) =>
					request.signal.addEventListener("abort", () => reject(request.signal.reason), { once: true }),
				);
				return json({ status: "ok" });
			}) as typeof fetch,
		};
		const dependency = createProviderEndpointHealthDependency(options);
		options.healthUrl = "https://changed.example/health";
		options.headers["x-host"] = "changed";

		const inspection = dependency.inspect?.(controller.signal);
		controller.abort(new Error("host stopped"));
		await expect(inspection).rejects.toThrow("host stopped");
		expect(dependency).toMatchObject({ id: "endpoint", kind: "local-endpoint", requirement: "required" });
		expect(Object.isFrozen(dependency)).toBe(true);
	});

	it("rejects invalid endpoint configuration before network effects", () => {
		const fetch = vi.fn() as typeof globalThis.fetch;
		expect(() =>
			createProviderEndpointHealthDependency({
				id: "endpoint",
				kind: "local-endpoint",
				requirement: "required",
				protocol: "openai-compatible",
				baseUrl: "http://models.example/v1",
				modelId: "model",
				maxResponseBytes: MAX_RESPONSE_BYTES,
				fetch,
			}),
		).toThrow();
		for (const baseUrl of ["https://models.example/v1?", "https://models.example/v1#"]) {
			expect(() =>
				createProviderEndpointHealthDependency({
					id: "endpoint",
					kind: "local-endpoint",
					requirement: "required",
					protocol: "openai-compatible",
					baseUrl,
					modelId: "model",
					maxResponseBytes: MAX_RESPONSE_BYTES,
					fetch,
				}),
			).toThrow();
		}
		expect(() =>
			createProviderEndpointHealthDependency({
				id: "endpoint",
				kind: "local-endpoint",
				requirement: "required",
				protocol: "llama.cpp",
				healthUrl: "https://models.example/health#",
				maxResponseBytes: MAX_RESPONSE_BYTES,
				fetch,
			}),
		).toThrow();
		expect(() =>
			createProviderEndpointHealthDependency({
				id: "endpoint",
				kind: "local-endpoint",
				requirement: "required",
				protocol: "ollama",
				baseUrl: "http://127.0.0.1:11434",
				modelId: "model",
				maxResponseBytes: 0,
				fetch,
			}),
		).toThrow();
		expect(fetch).not.toHaveBeenCalled();
	});
});

async function listen(server: Server): Promise<{ readonly server: Server; readonly url: string }> {
	await new Promise<void>((resolve, reject) => {
		server.once("error", reject);
		server.listen(0, "127.0.0.1", () => resolve());
	});
	const address = server.address();
	if (address === null || typeof address === "string") throw new Error("Loopback server did not expose a TCP port.");
	return { server, url: `http://127.0.0.1:${address.port}` };
}

async function close(server: Server): Promise<void> {
	await new Promise<void>((resolve, reject) => {
		server.close((error) => (error ? reject(error) : resolve()));
	});
}
