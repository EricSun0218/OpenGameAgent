import { afterEach, describe, expect, it, vi } from "vitest";
import { createMcpResourceScopedFetch } from "../src/mcp-scoped-fetch.js";
import { validateMcpSseRetryLine } from "../src/mcp-sse-retry.js";
import { createGameMcpStreamableHttpTransportFactory } from "../src/mcp-transports.js";

afterEach(() => {
	vi.restoreAllMocks();
});

describe("MCP HTTP header boundaries", () => {
	it("keeps configured and server-provided reconnection delays within the host timer policy", () => {
		const create = (maxDelayMs: number) =>
			createGameMcpStreamableHttpTransportFactory({
				url: "https://resource.example/mcp",
				maxHttpResponseBytes: 100,
				maxSseEventBytes: 100,
				reconnection: { initialDelayMs: 1, maxDelayMs, delayGrowFactor: 2, maxAttempts: 1 },
			});

		expect(() => create(2_147_483_648)).toThrowError(expect.objectContaining({ code: "invalid_configuration" }));
		expect(() => create(2_147_483_647)).not.toThrow();
		const encode = (value: string) => new TextEncoder().encode(value);
		expect(() => validateMcpSseRetryLine(encode("retry: 2\r\n"), 2)).not.toThrow();
		expect(() => validateMcpSseRetryLine(encode("retry: 0\n"), 2)).not.toThrow();
		expect(() => validateMcpSseRetryLine(encode("retry: invalid\n"), 2)).not.toThrow();
		expect(() => validateMcpSseRetryLine(encode("retry: 3\n"), 2)).toThrowError(
			expect.objectContaining({ code: "transport_limit_exceeded" }),
		);
		expect(() => validateMcpSseRetryLine(encode(`retry: ${"9".repeat(100)}\n`), 2)).toThrowError(
			expect.objectContaining({ code: "transport_limit_exceeded" }),
		);
		expect(() => validateMcpSseRetryLine(encode("\uFEFFretry: 3\n"), 2, true)).toThrowError(
			expect.objectContaining({ code: "transport_limit_exceeded" }),
		);
		expect(() => validateMcpSseRetryLine(encode("\uFEFFretry: 3\n"), 2, false)).not.toThrow();
	});

	it("snapshots HTTP reconnection fields once before creating transports", async () => {
		const reads = { initialDelayMs: 0, maxDelayMs: 0, delayGrowFactor: 0, maxAttempts: 0 };
		const values = { initialDelayMs: 1, maxDelayMs: 2, delayGrowFactor: 2, maxAttempts: 1 };
		const reconnection = {} as typeof values;
		for (const key of Object.keys(values) as (keyof typeof values)[]) {
			Object.defineProperty(reconnection, key, {
				get() {
					reads[key] += 1;
					return values[key];
				},
			});
		}
		const createTransport = createGameMcpStreamableHttpTransportFactory({
			url: "https://resource.example/mcp",
			maxHttpResponseBytes: 100,
			maxSseEventBytes: 100,
			reconnection,
		});
		values.maxDelayMs = 2_147_483_648;
		const transport = await createTransport(new AbortController().signal);
		const internal = transport as unknown as {
			readonly _reconnectionOptions: {
				readonly initialReconnectionDelay: number;
				readonly maxReconnectionDelay: number;
				readonly reconnectionDelayGrowFactor: number;
				readonly maxRetries: number;
			};
		};
		expect(reads).toEqual({ initialDelayMs: 1, maxDelayMs: 1, delayGrowFactor: 1, maxAttempts: 1 });
		expect(internal._reconnectionOptions).toEqual({
			initialReconnectionDelay: 1,
			maxReconnectionDelay: 2,
			reconnectionDelayGrowFactor: 2,
			maxRetries: 1,
		});
		await transport.close();
	});

	it("adds approved headers only to the exact resource endpoint", async () => {
		const calls: { readonly url: string; readonly headers: Headers }[] = [];
		const upstream = vi.fn<typeof fetch>(async (input, init) => {
			calls.push({
				url: inputHref(input),
				headers: inputHeaders(input, init),
			});
			return new Response(null, { status: 204 });
		});
		const scoped = createMcpResourceScopedFetch(
			upstream,
			new URL("https://resource.example/mcp"),
			new Headers({ Authorization: "Bearer resource-secret", "X-Host-Secret": "host-secret" }),
		);

		await scoped("https://resource.example/mcp", {
			headers: { Authorization: "Bearer oauth-token", "X-Request": "resource" },
		});
		await scoped("https://auth.example/token", {
			headers: { Authorization: "Basic oauth-client", "X-Request": "oauth" },
		});
		await scoped("https://resource.example/.well-known/oauth-protected-resource", {
			headers: { "X-Request": "metadata" },
		});

		expect(calls).toHaveLength(3);
		expect(calls[0]?.headers.get("authorization")).toBe("Bearer resource-secret");
		expect(calls[0]?.headers.get("x-host-secret")).toBe("host-secret");
		expect(calls[1]?.headers.get("authorization")).toBe("Basic oauth-client");
		expect(calls[1]?.headers.get("x-host-secret")).toBeNull();
		expect(calls[2]?.headers.get("x-host-secret")).toBeNull();
	});

	it("wires scoped headers into resource requests without putting them in OAuth requestInit", async () => {
		const calls: { readonly url: string; readonly headers: Headers }[] = [];
		const upstream = vi.fn<typeof fetch>(async (input, init) => {
			calls.push({ url: inputHref(input), headers: inputHeaders(input, init) });
			return new Response(null, { status: 202 });
		});
		const createTransport = createGameMcpStreamableHttpTransportFactory({
			url: "https://resource.example/mcp",
			headers: { "X-Host-Secret": "host-secret" },
			fetch: upstream,
			maxHttpResponseBytes: 1,
			maxSseEventBytes: 1,
			reconnection: { initialDelayMs: 1, maxDelayMs: 1, delayGrowFactor: 1, maxAttempts: 0 },
		});
		const transport = await createTransport(new AbortController().signal);

		await transport.send({
			jsonrpc: "2.0",
			method: "notifications/progress",
			params: { progressToken: "progress", progress: 1 },
		});
		await transport.close();

		expect(calls).toHaveLength(1);
		expect(calls[0]?.url).toBe("https://resource.example/mcp");
		expect(calls[0]?.headers.get("x-host-secret")).toBe("host-secret");
	});
});

type FetchInput = Parameters<typeof fetch>[0];

function inputHref(input: FetchInput): string {
	if (typeof input === "string") return input;
	if (input instanceof URL) return input.href;
	return input.url;
}

function inputHeaders(input: FetchInput, init: RequestInit | undefined): Headers {
	const headers = new Headers(typeof input === "string" || input instanceof URL ? undefined : input.headers);
	new Headers(init?.headers).forEach((value, key) => {
		headers.set(key, value);
	});
	return headers;
}
