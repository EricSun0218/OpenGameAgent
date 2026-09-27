import { Buffer } from "node:buffer";
import { type CredentialStore, InMemoryCredentialStore } from "@earendil-works/pi-ai";
import type { MediaProviderOperationContext, MediaProviderResource } from "@opengameagent/media";
import { describe, expect, it, vi } from "vitest";
import {
	type ArkImagesAdapterError,
	type ArkImagesProviderLimits,
	createArkImagesMediaProvider,
} from "../src/index.js";

const LIMITS: ArkImagesProviderLimits = Object.freeze({
	maxEndpointBytes: 256,
	maxCredentialReferenceBytes: 64,
	maxCredentialBytes: 128,
	maxModelBytes: 128,
	maxSizeBytes: 32,
	maxPromptBytes: 1_024,
	maxReferenceImages: 4,
	maxReferenceBytes: 1_024,
	maxReferenceTotalBytes: 4_096,
	maxRequestBytes: 16_384,
	maxResponseBytes: 16_384,
	maxResponseJsonDepth: 8,
	maxOutputs: 4,
	maxOutputBytes: 8_192,
	maxOutputUrlBytes: 1_024,
	requestTimeoutMs: 1_000,
});

describe("Ark images media provider", () => {
	it("rejects non-HTTPS endpoints and invalid mandatory limits at construction", () => {
		const credentials = new InMemoryCredentialStore();
		const create = (endpoint: string, limits: ArkImagesProviderLimits) =>
			providerFixture(credentials, { endpoint, limits });

		for (const endpoint of [
			"http://ark.example.test/api/v3/images/generations",
			"https://user:secret@ark.example.test/api/v3/images/generations",
			"https://ark.example.test/api/v3/images/generations?secret=value",
			"https://ark.example.test/api/v3/images/generations#fragment",
		]) {
			expect(() => create(endpoint, LIMITS)).toThrowError(
				expect.objectContaining<Partial<ArkImagesAdapterError>>({ code: "invalid_configuration" }),
			);
		}
		expect(() =>
			create("https://ark.example.test/api/v3/images/generations", { ...LIMITS, maxResponseBytes: 0 }),
		).toThrowError(expect.objectContaining<Partial<ArkImagesAdapterError>>({ code: "invalid_configuration" }));
		expect(() =>
			create("https://ark.example.test/api/v3/images/generations", {
				...LIMITS,
				requestTimeoutMs: 2_147_483_648,
			}),
		).toThrowError(expect.objectContaining<Partial<ArkImagesAdapterError>>({ code: "invalid_configuration" }));
		const unicodeEndpoint = "https://例.example.test/api/v3/images/generations";
		expect(() =>
			create(unicodeEndpoint, {
				...LIMITS,
				maxEndpointBytes: Buffer.byteLength(unicodeEndpoint, "utf8"),
			}),
		).toThrowError(expect.objectContaining<Partial<ArkImagesAdapterError>>({ code: "invalid_configuration" }));
	});

	it("resolves an API-key reference and maps a bounded b64_json image response", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "ark-secret" }));
		const upstream = vi.fn<typeof fetch>(async (input) => {
			const request = input instanceof Request ? input : new Request(input);
			expect(request.url).toBe("https://ark.example.test/api/v3/images/generations");
			expect(request.method).toBe("POST");
			expect(request.headers.get("authorization")).toBe("Bearer ark-secret");
			expect(request.headers.get("accept")).toBe("application/json");
			expect(request.headers.get("content-type")).toBe("application/json");
			expect(await request.json()).toEqual({
				model: "doubao-seedream-test",
				output_format: "png",
				prompt: "a painted mountain village",
				response_format: "b64_json",
				size: "1024x1024",
				watermark: false,
			});
			return Response.json({
				model: "doubao-seedream-test",
				created: 1,
				data: [{ b64_json: Buffer.from([1, 2, 3]).toString("base64"), output_format: "png" }],
			});
		});
		const provider = providerFixture(credentials, { fetch: upstream });

		const result = await provider.start(
			{ kind: "image", input: { input: [{ type: "text", text: "a painted mountain village" }] } },
			operationContext(),
		);

		expect(result.status).toBe("succeeded");
		if (result.status !== "succeeded") throw new Error("Expected a successful image response.");
		expect(result.resources).toHaveLength(1);
		expect(result.resources[0]).toMatchObject({
			ordinal: 0,
			kind: "image",
			mediaType: "image/png",
			declaredBytes: 3,
		});
		expect(await collect(result.resources[0])).toEqual(Buffer.from([1, 2, 3]));
		expect(upstream).toHaveBeenCalledOnce();
	});

	it("maps one prompt and bounded reference images to Ark data URLs", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "ark-secret" }));
		const upstream = vi.fn<typeof fetch>(async (input) => {
			const request = input instanceof Request ? input : new Request(input);
			expect(await request.json()).toEqual({
				image: ["data:image/png;base64,AQID", "data:image/jpeg;base64,BAUG"],
				model: "doubao-seedream-test",
				output_format: "jpeg",
				prompt: "restyle these references",
				response_format: "b64_json",
				size: "2K",
				watermark: true,
			});
			return Response.json({
				model: "doubao-seedream-test",
				created: 1,
				data: [{ b64_json: "AQID", output_format: "jpeg" }],
			});
		});
		const provider = providerFixture(credentials, {
			outputFormat: "jpeg",
			size: "2K",
			watermark: true,
			fetch: upstream,
		});

		const result = await provider.start(
			{
				kind: "image",
				input: {
					input: [
						{ type: "text", text: "restyle these references" },
						{ type: "image", mediaType: "image/png", data: "AQID" },
						{ type: "image", mediaType: "image/jpeg", data: "BAUG" },
					],
				},
			},
			operationContext(),
		);

		expect(result.status).toBe("succeeded");
		expect(upstream).toHaveBeenCalledOnce();
	});

	it("rejects an Ark success envelope without a valid creation timestamp", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "ark-secret" }));
		const upstream = vi.fn<typeof fetch>(async () =>
			Response.json({
				model: "doubao-seedream-test",
				data: [{ b64_json: "AQID", output_format: "png" }],
			}),
		);
		const provider = providerFixture(credentials, { fetch: upstream });

		await expect(
			provider.start({ kind: "image", input: { input: [{ type: "text", text: "prompt" }] } }, operationContext()),
		).resolves.toEqual({ status: "failed", failure: "malformed" });
	});

	it("accepts the model name returned for a configured Ark Endpoint ID", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "ark-secret" }));
		const upstream = vi.fn<typeof fetch>(async () =>
			Response.json({
				model: "doubao-seedream-4-0-250828",
				created: 1,
				data: [{ b64_json: "AQID", output_format: "png" }],
			}),
		);
		const provider = providerFixture(credentials, {
			modelId: "ep-2025-example",
			fetch: upstream,
		});

		await expect(
			provider.start({ kind: "image", input: { input: [{ type: "text", text: "prompt" }] } }, operationContext()),
		).resolves.toMatchObject({ status: "succeeded" });
	});

	it("rejects aggregate reference bytes as capacity before credential resolution or HTTP", async () => {
		const credentials = new InMemoryCredentialStore();
		const read = vi.spyOn(credentials, "read");
		const upstream = vi.fn<typeof fetch>();
		const provider = providerFixture(credentials, {
			limits: { ...LIMITS, maxReferenceTotalBytes: 5 },
			fetch: upstream,
		});

		await expect(
			provider.start(
				{
					kind: "image",
					input: {
						input: [
							{ type: "text", text: "prompt" },
							{ type: "image", mediaType: "image/png", data: "AQID" },
							{ type: "image", mediaType: "image/png", data: "BAUG" },
						],
					},
				},
				operationContext(),
			),
		).resolves.toEqual({ status: "failed", failure: "capacity" });
		expect(read).not.toHaveBeenCalled();
		expect(upstream).not.toHaveBeenCalled();
	});

	it("rejects the complete serialized request before credential resolution or HTTP", async () => {
		const credentials = new InMemoryCredentialStore();
		const read = vi.spyOn(credentials, "read");
		const upstream = vi.fn<typeof fetch>();
		const expectedBody = {
			model: "doubao-seedream-test",
			output_format: "png",
			prompt: "prompt",
			response_format: "b64_json",
			size: "1024x1024",
			watermark: false,
		};
		const provider = providerFixture(credentials, {
			limits: { ...LIMITS, maxRequestBytes: Buffer.byteLength(JSON.stringify(expectedBody)) - 1 },
			fetch: upstream,
		});

		await expect(
			provider.start({ kind: "image", input: { input: [{ type: "text", text: "prompt" }] } }, operationContext()),
		).resolves.toEqual({ status: "failed", failure: "capacity" });
		expect(read).not.toHaveBeenCalled();
		expect(upstream).not.toHaveBeenCalled();
	});

	it("rejects an oversized response body before JSON parsing", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "ark-secret" }));
		const responseBody = JSON.stringify({
			model: "doubao-seedream-test",
			created: 1,
			data: [{ b64_json: "AQID", output_format: "png" }],
		});
		const upstream = vi.fn<typeof fetch>(
			async () =>
				new Response(responseBody, {
					headers: {
						"content-length": String(Buffer.byteLength(responseBody)),
						"content-type": "application/json",
					},
				}),
		);
		const provider = providerFixture(credentials, {
			limits: { ...LIMITS, maxResponseBytes: Buffer.byteLength(responseBody) - 1 },
			fetch: upstream,
		});

		await expect(
			provider.start({ kind: "image", input: { input: [{ type: "text", text: "prompt" }] } }, operationContext()),
		).resolves.toEqual({ status: "failed", failure: "capacity" });
		expect(upstream).toHaveBeenCalledOnce();
	});

	it("maps a streamed response overflow to capacity without relying on Content-Length", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "ark-secret" }));
		const responseBody = JSON.stringify({
			model: "doubao-seedream-test",
			created: 1,
			data: [{ b64_json: "AQID", output_format: "png" }],
		});
		const bytes = new TextEncoder().encode(responseBody);
		const upstream = vi.fn<typeof fetch>(
			async () =>
				new Response(
					new ReadableStream({
						start(controller) {
							controller.enqueue(bytes);
							controller.close();
						},
					}),
					{ headers: { "content-type": "application/json" } },
				),
		);
		const provider = providerFixture(credentials, {
			limits: { ...LIMITS, maxResponseBytes: bytes.byteLength - 1 },
			fetch: upstream,
		});

		await expect(
			provider.start({ kind: "image", input: { input: [{ type: "text", text: "prompt" }] } }, operationContext()),
		).resolves.toEqual({ status: "failed", failure: "capacity" });
	});

	it("redacts a response-stream failure as an unresolved transport error", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "ark-secret" }));
		const upstream = vi.fn<typeof fetch>(
			async () =>
				new Response(
					new ReadableStream({
						start(controller) {
							controller.error(new Error("private upstream socket and request detail"));
						},
					}),
					{ headers: { "content-type": "application/json" } },
				),
		);
		const provider = providerFixture(credentials, { fetch: upstream });

		const failure = await provider
			.start({ kind: "image", input: { input: [{ type: "text", text: "prompt" }] } }, operationContext())
			.catch((error: unknown) => error);

		expect(failure).toMatchObject<Partial<ArkImagesAdapterError>>({ code: "transport" });
		expect(String(failure)).not.toContain("private upstream socket and request detail");
	});

	it("maps mixed image slots to a truthful partial result without provider error text", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "ark-secret" }));
		const upstream = vi.fn<typeof fetch>(async () =>
			Response.json({
				model: "doubao-seedream-test",
				created: 1,
				data: [
					{ b64_json: "AQID", output_format: "png" },
					{ error: { code: "OutputBlocked", message: "private provider detail" } },
					{ b64_json: "not canonical base64", output_format: "png" },
				],
			}),
		);
		const provider = providerFixture(credentials, { fetch: upstream });

		const result = await provider.start(
			{ kind: "image", input: { input: [{ type: "text", text: "prompt" }] } },
			operationContext(),
		);

		expect(result).toMatchObject({
			status: "partially_succeeded",
			resources: [{ ordinal: 0, declaredBytes: 3, mediaType: "image/png" }],
			failures: [
				{ ordinal: 1, failure: "provider" },
				{ ordinal: 2, failure: "malformed" },
			],
		});
		expect(JSON.stringify(result)).not.toContain("private provider detail");
	});

	it("enforces host-selected output count and decoded-byte budgets", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "ark-secret" }));
		const upstream = vi
			.fn<typeof fetch>()
			.mockResolvedValueOnce(
				Response.json({
					model: "doubao-seedream-test",
					created: 1,
					data: [
						{ b64_json: "AQID", output_format: "png" },
						{ b64_json: "BAUG", output_format: "png" },
					],
				}),
			)
			.mockResolvedValueOnce(
				Response.json({
					model: "doubao-seedream-test",
					created: 1,
					data: [{ b64_json: "AQID", output_format: "png" }],
				}),
			);
		const request = { kind: "image" as const, input: { input: [{ type: "text", text: "prompt" }] } };
		const create = (limits: ArkImagesProviderLimits) => providerFixture(credentials, { limits, fetch: upstream });

		await expect(create({ ...LIMITS, maxOutputs: 1 }).start(request, operationContext())).resolves.toEqual({
			status: "failed",
			failure: "capacity",
		});
		await expect(create({ ...LIMITS, maxOutputBytes: 2 }).start(request, operationContext())).resolves.toEqual({
			status: "failed",
			failure: "capacity",
		});
	});

	it("rejects duplicate JSON keys and non-JSON response media types", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "ark-secret" }));
		const body =
			'{"model":"wrong","model":"doubao-seedream-test","created":1,"data":[{"b64_json":"AQID","output_format":"png"}]}';
		const upstream = vi
			.fn<typeof fetch>()
			.mockResolvedValueOnce(new Response(body, { headers: { "content-type": "application/json" } }))
			.mockResolvedValueOnce(
				new Response(body.replace('"wrong","model":', ""), { headers: { "content-type": "text/plain" } }),
			);
		const provider = providerFixture(credentials, { fetch: upstream });
		const request = { kind: "image" as const, input: { input: [{ type: "text", text: "prompt" }] } };

		await expect(provider.start(request, operationContext())).resolves.toEqual({
			status: "failed",
			failure: "malformed",
		});
		await expect(provider.start(request, operationContext())).resolves.toEqual({
			status: "failed",
			failure: "malformed",
		});
	});

	it("maps the configured response JSON-depth limit to capacity", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "ark-secret" }));
		const upstream = vi.fn<typeof fetch>(async () =>
			Response.json({
				model: "doubao-seedream-test",
				created: 1,
				data: [{ b64_json: "AQID", output_format: "png" }],
			}),
		);
		const provider = providerFixture(credentials, {
			limits: { ...LIMITS, maxResponseJsonDepth: 2 },
			fetch: upstream,
		});

		await expect(
			provider.start({ kind: "image", input: { input: [{ type: "text", text: "prompt" }] } }, operationContext()),
		).resolves.toEqual({ status: "failed", failure: "capacity" });
	});

	it("times out a non-cooperative fetch and aborts its request signal", async () => {
		vi.useFakeTimers();
		try {
			const credentials = new InMemoryCredentialStore();
			await credentials.modify("ark-account", async () => ({ type: "api_key", key: "ark-secret" }));
			let requestSignal: AbortSignal | undefined;
			const upstream = vi.fn<typeof fetch>((input) => {
				requestSignal = input instanceof Request ? input.signal : undefined;
				return new Promise<Response>(() => undefined);
			});
			const provider = providerFixture(credentials, {
				limits: { ...LIMITS, requestTimeoutMs: 10 },
				fetch: upstream,
			});
			const pending = provider.start(
				{ kind: "image", input: { input: [{ type: "text", text: "prompt" }] } },
				operationContext(),
			);
			const rejection = expect(pending).rejects.toMatchObject({
				code: "request_timeout",
			} satisfies Partial<ArkImagesAdapterError>);
			await vi.advanceTimersByTimeAsync(10);

			await rejection;
			expect(requestSignal?.aborted).toBe(true);
		} finally {
			vi.useRealTimers();
		}
	});

	it("preserves caller cancellation while a provider fetch is non-cooperative", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "ark-secret" }));
		let requestSignal: AbortSignal | undefined;
		const upstream = vi.fn<typeof fetch>((input) => {
			requestSignal = input instanceof Request ? input.signal : undefined;
			return new Promise<Response>(() => undefined);
		});
		const provider = providerFixture(credentials, { fetch: upstream });
		const controller = new AbortController();
		const reason = new Error("caller stopped this exact attempt");
		const pending = provider.start(
			{ kind: "image", input: { input: [{ type: "text", text: "prompt" }] } },
			operationContext(controller.signal),
		);
		await vi.waitFor(() => expect(upstream).toHaveBeenCalledOnce());

		controller.abort(reason);

		await expect(pending).rejects.toBe(reason);
		expect(requestSignal?.reason).toBe(reason);
	});

	it("does not let non-cooperative error-body cleanup delay a terminal failure", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "ark-secret" }));
		const cleanup = Promise.withResolvers<void>();
		let cleanupStarted = false;
		const upstream = vi.fn<typeof fetch>(
			async () =>
				new Response(
					new ReadableStream({
						cancel: () => {
							cleanupStarted = true;
							return cleanup.promise;
						},
					}),
					{ status: 500, headers: { "content-type": "application/json" } },
				),
		);
		const provider = providerFixture(credentials, { fetch: upstream });
		const observed = provider
			.start({ kind: "image", input: { input: [{ type: "text", text: "prompt" }] } }, operationContext())
			.then(
				(result) => ({ kind: "resolved" as const, result }),
				(error: unknown) => ({ kind: "rejected" as const, error }),
			);
		await vi.waitFor(() => expect(cleanupStarted).toBe(true));
		const cleanupBlocked = Symbol("cleanup-blocked");

		const outcome = await Promise.race([observed, Promise.resolve(cleanupBlocked)]);
		cleanup.resolve();
		await observed;

		expect(outcome).not.toBe(cleanupBlocked);
		if (outcome === cleanupBlocked) throw new Error("Response cleanup blocked the terminal failure.");
		expect(outcome).toEqual({ kind: "resolved", result: { status: "failed", failure: "provider" } });
	});

	it("rejects an oversized stored credential before HTTP without exposing it", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "oversized-secret" }));
		const upstream = vi.fn<typeof fetch>();
		const provider = providerFixture(credentials, {
			limits: { ...LIMITS, maxCredentialBytes: Buffer.byteLength("oversized-secret") - 1 },
			fetch: upstream,
		});

		const result = await provider.start(
			{ kind: "image", input: { input: [{ type: "text", text: "prompt" }] } },
			operationContext(),
		);

		expect(result).toEqual({ status: "failed", failure: "capacity" });
		expect(JSON.stringify(result)).not.toContain("oversized-secret");
		expect(upstream).not.toHaveBeenCalled();
	});

	it("keeps URL output retrieval disabled when no host acceptance policy exists", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "ark-secret" }));
		const upstream = vi.fn<typeof fetch>(async () =>
			Response.json({
				model: "doubao-seedream-test",
				created: 1,
				data: [{ url: "https://cdn.example.test/generated.png", output_format: "png" }],
			}),
		);
		const provider = providerFixture(credentials, { fetch: upstream });

		await expect(
			provider.start({ kind: "image", input: { input: [{ type: "text", text: "prompt" }] } }, operationContext()),
		).resolves.toEqual({ status: "failed", failure: "resource" });
		expect(upstream).toHaveBeenCalledOnce();
	});

	it("bounds the canonical URL before asking the host to accept an output", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "ark-secret" }));
		const outputUrl = "https://例.example.test/generated.png";
		const upstream = vi.fn<typeof fetch>(async () =>
			Response.json({
				model: "doubao-seedream-test",
				created: 1,
				data: [{ url: outputUrl, output_format: "png" }],
			}),
		);
		const accept = vi.fn(async () => true);
		const outputFetch = vi.fn<typeof fetch>();
		const provider = providerFixture(credentials, {
			limits: { ...LIMITS, maxOutputUrlBytes: Buffer.byteLength(outputUrl, "utf8") },
			fetch: upstream,
			urlOutputs: { accept, fetch: outputFetch },
		});

		await expect(
			provider.start({ kind: "image", input: { input: [{ type: "text", text: "prompt" }] } }, operationContext()),
		).resolves.toEqual({ status: "failed", failure: "capacity" });
		expect(accept).not.toHaveBeenCalled();
		expect(outputFetch).not.toHaveBeenCalled();
	});

	it("retrieves an accepted URL output with no Ark credential and no redirects", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "ark-secret" }));
		const outputUrl = "https://cdn.example.test/generated.png?signature=opaque";
		const upstream = vi
			.fn<typeof fetch>()
			.mockResolvedValueOnce(
				Response.json({
					model: "doubao-seedream-test",
					created: 1,
					data: [{ url: outputUrl, output_format: "png" }],
				}),
			)
			.mockImplementationOnce(async (input) => {
				const request = input instanceof Request ? input : new Request(input);
				expect(request.url).toBe(outputUrl);
				expect(request.method).toBe("GET");
				expect(request.redirect).toBe("manual");
				expect(request.credentials).toBe("omit");
				expect(request.headers.get("authorization")).toBeNull();
				return new Response(Uint8Array.from([1, 2, 3]), { headers: { "content-type": "image/png" } });
			});
		const accept = vi.fn(async (url: string) => url === outputUrl);
		const provider = providerFixture(credentials, {
			fetch: upstream,
			urlOutputs: { accept, fetch: upstream },
		});

		const result = await provider.start(
			{ kind: "image", input: { input: [{ type: "text", text: "prompt" }] } },
			operationContext(),
		);

		expect(result.status).toBe("succeeded");
		if (result.status !== "succeeded") throw new Error("Expected a successful URL output.");
		expect(await collect(result.resources[0])).toEqual(Buffer.from([1, 2, 3]));
		expect(accept).toHaveBeenCalledWith(outputUrl, expect.any(AbortSignal));
		expect(upstream).toHaveBeenCalledTimes(2);
	});

	it("does not follow a redirect while retrieving an accepted URL output", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "ark-secret" }));
		const outputUrl = "https://cdn.example.test/generated.png";
		const upstream = vi
			.fn<typeof fetch>()
			.mockResolvedValueOnce(
				Response.json({
					model: "doubao-seedream-test",
					created: 1,
					data: [{ url: outputUrl, output_format: "png" }],
				}),
			)
			.mockResolvedValueOnce(
				new Response(null, {
					status: 302,
					headers: { location: "https://unapproved.example.test/generated.png" },
				}),
			);
		const provider = providerFixture(credentials, {
			fetch: upstream,
			urlOutputs: { accept: async () => true, fetch: upstream },
		});

		await expect(
			provider.start({ kind: "image", input: { input: [{ type: "text", text: "prompt" }] } }, operationContext()),
		).resolves.toEqual({ status: "failed", failure: "resource" });
		expect(upstream).toHaveBeenCalledTimes(2);
	});

	it("maps an official top-level Ark error to a redacted provider failure", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "ark-secret" }));
		const upstream = vi.fn<typeof fetch>(async () =>
			Response.json({
				model: "doubao-seedream-test",
				created: 1,
				data: null,
				error: { code: "SensitiveProviderCode", message: "private provider failure detail" },
			}),
		);
		const provider = providerFixture(credentials, { fetch: upstream });

		const result = await provider.start(
			{ kind: "image", input: { input: [{ type: "text", text: "prompt" }] } },
			operationContext(),
		);

		expect(result).toEqual({ status: "failed", failure: "provider" });
		expect(JSON.stringify(result)).not.toContain("private provider failure detail");
	});

	it("redacts credential-store failures behind a stable transport error", async () => {
		const credentials: CredentialStore = {
			read: async () => {
				throw new Error("private credential path and account");
			},
			list: async () => [],
			modify: async () => undefined,
			delete: async () => undefined,
		};
		const upstream = vi.fn<typeof fetch>();
		const provider = providerFixture(credentials, { fetch: upstream });

		const failure = await provider
			.start({ kind: "image", input: { input: [{ type: "text", text: "prompt" }] } }, operationContext())
			.catch((error: unknown) => error);

		expect(failure).toMatchObject<Partial<ArkImagesAdapterError>>({ code: "transport" });
		expect(String(failure)).not.toContain("private credential path and account");
		expect(upstream).not.toHaveBeenCalled();
	});

	it("does not retry or expose a provider transport failure", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "ark-secret" }));
		const upstream = vi.fn<typeof fetch>(async () => {
			throw new Error("private proxy address and request detail");
		});
		const provider = providerFixture(credentials, { fetch: upstream });

		const failure = await provider
			.start({ kind: "image", input: { input: [{ type: "text", text: "prompt" }] } }, operationContext())
			.catch((error: unknown) => error);

		expect(failure).toMatchObject<Partial<ArkImagesAdapterError>>({ code: "transport" });
		expect(String(failure)).not.toContain("private proxy address and request detail");
		expect(upstream).toHaveBeenCalledOnce();
	});

	it("rejects a credential that cannot be represented as one Authorization header", async () => {
		const credentials = new InMemoryCredentialStore();
		await credentials.modify("ark-account", async () => ({ type: "api_key", key: "bad\nsecret" }));
		const upstream = vi.fn<typeof fetch>();
		const provider = providerFixture(credentials, { fetch: upstream });

		await expect(
			provider.start({ kind: "image", input: { input: [{ type: "text", text: "prompt" }] } }, operationContext()),
		).resolves.toEqual({ status: "failed", failure: "authentication" });
		expect(upstream).not.toHaveBeenCalled();
	});
});

function operationContext(signal = new AbortController().signal): MediaProviderOperationContext {
	return { jobId: "job", attemptId: "attempt", submissionKey: "submission", signal };
}

type ArkProviderOverrides = Partial<Omit<Parameters<typeof createArkImagesMediaProvider>[0], "credentials">>;

function providerFixture(credentials: CredentialStore, overrides: ArkProviderOverrides = {}) {
	return createArkImagesMediaProvider({
		credentialReference: "ark-account",
		endpoint: "https://ark.example.test/api/v3/images/generations",
		modelId: "doubao-seedream-test",
		outputFormat: "png",
		size: "1024x1024",
		watermark: false,
		limits: LIMITS,
		...overrides,
		credentials,
	});
}

async function collect(resource: MediaProviderResource | undefined): Promise<Buffer> {
	if (!resource) throw new Error("Expected a generated resource.");
	if (resource.bytes instanceof Uint8Array) return Buffer.from(resource.bytes);
	if ("read" in resource.bytes) return Buffer.from(await resource.bytes.read(new AbortController().signal));
	const chunks: Uint8Array[] = [];
	for await (const chunk of resource.bytes) chunks.push(chunk);
	return Buffer.concat(chunks);
}
