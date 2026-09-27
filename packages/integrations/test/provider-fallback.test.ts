import {
	type AssistantMessage,
	type AssistantMessageEvent,
	type AssistantMessageEventStream,
	createAssistantMessageEventStream,
	type Model,
	type Provider,
} from "@earendil-works/pi-ai";
import type { GameProviderAttemptLifecycle, GameProviderAttemptStreamFn } from "@opengameagent/agent";
import { RuntimeHealthMonitor } from "@opengameagent/health";
import fc from "fast-check";
import { describe, expect, test } from "vitest";
import {
	createProviderFailureEvidenceChannel,
	type ProviderFailureEvidenceReader,
	type ProviderFailureFact,
	type ProviderFallbackFailureKind,
} from "../src/provider-failures.js";
import {
	captureProviderProfileSnapshot,
	createProviderProfileModelStream,
	type ProviderProfileConfiguration,
} from "../src/provider-profiles.js";

const CONTEXT = { messages: [] };
const ALL_TRANSIENT_FAILURES = [
	"throttled",
	"timeout",
	"transport",
	"capacity",
	"provider-internal",
] as const satisfies readonly ProviderFallbackFailureKind[];
const PI_STOP_REASONS = new Set(["pending", "stop", "length", "toolUse", "error", "aborted", "deferred"]);
const unknownStopReason = fc.string().filter((reason) => !PI_STOP_REASONS.has(reason));
const malformedTerminal = fc.oneof(
	fc.record({
		delivery: fc.constant("done" as const),
		reason: fc.oneof(fc.constantFrom("pending", "error", "aborted"), unknownStopReason),
	}),
	fc.record({
		delivery: fc.constant("error" as const),
		reason: fc.oneof(fc.constantFrom("pending", "stop", "length", "toolUse", "deferred"), unknownStopReason),
	}),
	fc.record({
		delivery: fc.constant("result" as const),
		reason: fc.oneof(fc.constant("pending"), unknownStopReason),
	}),
);
const legalTerminal = fc.oneof(
	fc.record({
		kind: fc.constant("done" as const),
		delivery: fc.constantFrom("event" as const, "result" as const),
		reason: fc.constantFrom("stop" as const, "length" as const, "toolUse" as const, "deferred" as const),
	}),
	fc.record({
		kind: fc.constant("error" as const),
		delivery: fc.constantFrom("event" as const, "result" as const),
		reason: fc.constantFrom("error" as const, "aborted" as const),
	}),
);
const semanticEventType = fc.constantFrom(
	"text_start",
	"text_delta",
	"text_end",
	"thinking_start",
	"thinking_delta",
	"thinking_end",
	"toolcall_start",
	"toolcall_delta",
	"toolcall_end",
);

describe("typed presemantic provider fallback", () => {
	test("projects fresh real provider success without issuing a health request", async () => {
		let monotonicNow = 100;
		const primary = routeFixture("primary", (model) => successOutcome(model, "primary"));
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([primary], [target(primary)]),
			health: {
				id: "model-route",
				requirement: "required",
				maxObservationAgeMs: 5,
				now: () => monotonicNow,
			},
		});
		const health = new RuntimeHealthMonitor({
			dependencies: modelStream.healthDependencies(),
			limits: {
				maxDependencies: 1,
				maxDependencyIdBytes: 64,
				maxConcurrentInspections: 1,
				inspectionTimeoutMs: 1_000,
			},
			now: () => Date.parse("2026-09-01T00:00:00.000Z"),
		});

		await expect(health.inspect()).resolves.toMatchObject({
			status: "not-ready",
			dependencies: [{ kind: "provider", state: "declared" }],
		});
		expect(primary.observed.streams).toBe(0);

		const stream = await attemptStream(modelStream)(primary.model, CONTEXT, {}, recordingAttempts().lifecycle);
		await stream.result();
		await expect(health.inspect()).resolves.toMatchObject({
			status: "ready",
			dependencies: [{ state: "ready" }],
		});
		expect(primary.observed.streams).toBe(1);

		monotonicNow = 106;
		await expect(health.inspect()).resolves.toMatchObject({
			status: "not-ready",
			dependencies: [{ state: "available", reason: "stale" }],
		});
		expect(primary.observed.streams).toBe(1);
	});

	test("maps a successful fallback to degraded and an exact terminal auth failure to unavailable", async () => {
		const first = routeFixture("primary", (model) =>
			failureOutcome(model, "primary", { type: "timeout", usage: "unreported" }),
		);
		const second = routeFixture("secondary", (model) => successOutcome(model, "secondary"));
		const fallback = createProviderProfileModelStream({
			configuration: () => configuration([first, second], [target(first, ["timeout"]), target(second)]),
			health: {
				id: "fallback-route",
				requirement: "required",
				maxObservationAgeMs: 10,
				now: () => 1,
			},
		});
		const fallbackStream = await attemptStream(fallback)(first.model, CONTEXT, {}, recordingAttempts().lifecycle);
		await fallbackStream.result();
		await expect(fallback.healthDependencies()[0]?.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "degraded",
			reason: "dependency-failure",
		});

		const denied = routeFixture("denied", (model) =>
			failureOutcome(model, "denied", { type: "http", status: 401, usage: "unreported" }),
		);
		const terminal = createProviderProfileModelStream({
			configuration: () => configuration([denied], [target(denied)]),
			health: {
				id: "denied-route",
				requirement: "required",
				maxObservationAgeMs: 10,
				now: () => 1,
			},
		});
		const deniedStream = await attemptStream(terminal)(denied.model, CONTEXT, {}, recordingAttempts().lifecycle);
		await deniedStream.result();
		await expect(terminal.healthDependencies()[0]?.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "unavailable",
			reason: "authentication-required",
		});
	});

	test("replaces prior readiness after untyped provider start failures and snapshots health once", async () => {
		const startFailure = new Error("provider start rejected");
		let rejectStart = false;
		let healthReads = 0;
		const primary = routeFixture("primary", (model) => {
			if (rejectStart) throw startFailure;
			return successOutcome(model, "primary");
		});
		const health = {
			id: "provider-start",
			requirement: "required" as const,
			maxObservationAgeMs: 10,
			now: () => 1,
		};
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([primary], [target(primary)]),
			get health() {
				healthReads += 1;
				return health;
			},
		});
		expect(healthReads).toBe(1);

		const succeeded = await attemptStream(modelStream)(primary.model, CONTEXT, {}, recordingAttempts().lifecycle);
		await succeeded.result();
		await expect(modelStream.healthDependencies()[0]?.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "ready",
		});

		rejectStart = true;
		const rejected = await attemptStream(modelStream)(primary.model, CONTEXT, {}, recordingAttempts().lifecycle);
		await expect(rejected.result()).resolves.toMatchObject({ provider: "primary", stopReason: "error" });
		await expect(modelStream.healthDependencies()[0]?.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "unavailable",
			reason: "unknown",
		});

		rejectStart = false;
		const restored = await attemptStream(modelStream)(primary.model, CONTEXT, {}, recordingAttempts().lifecycle);
		await restored.result();
		const rejectedCommit: GameProviderAttemptLifecycle = {
			begin: () => ({
				index: "1",
				discard: () => undefined,
				captureTerminal: () => undefined,
				commit: async () => {
					throw startFailure;
				},
				settle: async () => undefined,
			}),
		};
		await expect(attemptStream(modelStream)(primary.model, CONTEXT, {}, rejectedCommit)).rejects.toBe(startFailure);
		await expect(modelStream.healthDependencies()[0]?.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "ready",
		});

		const optionFailure = new Error("request options unavailable");
		const requestOptions = Object.defineProperty({}, "apiKey", {
			enumerable: true,
			get() {
				throw optionFailure;
			},
		}) as Parameters<GameProviderAttemptStreamFn>[2];
		const streamsBeforeOptions = primary.observed.streams;
		await expect(
			attemptStream(modelStream)(primary.model, CONTEXT, requestOptions, recordingAttempts().lifecycle),
		).rejects.toBe(optionFailure);
		expect(primary.observed.streams).toBe(streamsBeforeOptions);
		await expect(modelStream.healthDependencies()[0]?.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "ready",
		});
	});

	test("records an untyped fallback start rejection without retrying it", async () => {
		const first = routeFixture("primary", (model) =>
			failureOutcome(model, "primary", { type: "timeout", usage: "unreported" }),
		);
		const fallbackFailure = new Error("fallback start rejected");
		const second = routeFixture("secondary", () => {
			throw fallbackFailure;
		});
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first, second], [target(first, ["timeout"]), target(second)]),
			health: {
				id: "fallback-start",
				requirement: "required",
				maxObservationAgeMs: 10,
				now: () => 1,
			},
		});

		const stream = await attemptStream(modelStream)(first.model, CONTEXT, {}, recordingAttempts().lifecycle);
		await expect(stream.result()).resolves.toMatchObject({ provider: "secondary", stopReason: "error" });
		expect(second.observed.streams).toBe(1);
		await expect(modelStream.healthDependencies()[0]?.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "unavailable",
			reason: "unknown",
		});
	});

	test("replaces prior readiness when the provider event iterator rejects", async () => {
		const streamFailure = new Error("provider stream failed");
		let rejectStream = false;
		const original = routeFixture("primary", (model) => successOutcome(model, "primary"));
		const originalStream = original.provider.streamSimple;
		const provider: Provider = {
			...original.provider,
			streamSimple: ((...parameters: Parameters<NonNullable<Provider["streamSimple"]>>) => {
				if (!rejectStream) return originalStream(...parameters);
				return {
					[Symbol.asyncIterator]: () => ({
						next: async () => {
							throw streamFailure;
						},
					}),
					result: async () => {
						throw streamFailure;
					},
				} as unknown as AssistantMessageEventStream;
			}) as Provider["streamSimple"],
		};
		const primary: RouteFixture = { ...original, provider };
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([primary], [target(primary)]),
			health: {
				id: "provider-stream",
				requirement: "required",
				maxObservationAgeMs: 10,
				now: () => 1,
			},
		});

		const succeeded = await attemptStream(modelStream)(primary.model, CONTEXT, {}, recordingAttempts().lifecycle);
		await succeeded.result();
		rejectStream = true;
		const failed = await attemptStream(modelStream)(primary.model, CONTEXT, {}, recordingAttempts().lifecycle);
		await expect(failed.result()).resolves.toMatchObject({ provider: "primary", stopReason: "error" });
		await expect(modelStream.healthDependencies()[0]?.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "unavailable",
			reason: "unknown",
		});
	});

	test.each([
		["throttled", { type: "http", status: 429, usage: "reported" }],
		["timeout", { type: "timeout", usage: "reported" }],
		["transport", { type: "transport", usage: "reported" }],
		["capacity", { type: "capacity", usage: "reported" }],
		["provider-internal", { type: "http", status: 503, usage: "reported" }],
	] as const)("advances after an admitted typed %s failure and keeps each target visible", async (kind, fact) => {
		const first = routeFixture("primary", (model) => failureOutcome(model, "primary", fact, { input: 7, output: 3 }));
		const second = routeFixture("secondary", (model) => successOutcome(model, "secondary"));
		const attempts = recordingAttempts();
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first, second], [target(first, [kind]), target(second)]),
		});

		const stream = await attemptStream(modelStream)(first.model, CONTEXT, {}, attempts.lifecycle);
		const events = await collect(stream);

		expect(events.map((event) => event.type)).toEqual(["start", "done"]);
		expect(events[0]?.type === "start" ? events[0].partial.provider : undefined).toBe("secondary");
		expect(await stream.result()).toMatchObject({
			provider: "secondary",
			content: [{ type: "text", text: "secondary" }],
		});
		expect(first.observed).toEqual({ streams: 1, maxRetries: [0] });
		expect(second.observed).toEqual({ streams: 1, maxRetries: [0] });
		expect(attempts.records.map(({ provider, settled }) => ({ provider, settled: settled?.stopReason }))).toEqual([
			{ provider: "primary", settled: "error" },
			{ provider: "secondary", settled: undefined },
		]);
		expect(attempts.records[0]?.settled?.usage).toMatchObject({ input: 7, output: 3, totalTokens: 10 });
		expect(attempts.records[0]?.captured).toMatchObject({ usage: "reported" });
		expect(attempts.records[0]?.captured?.message).toBe(attempts.records[0]?.settled);
	});

	test.each([
		["credential", { type: "credential", usage: "unreported" }],
		["authentication", { type: "http", status: 401, usage: "unreported" }],
		["permission", { type: "http", status: 403, usage: "unreported" }],
		["quota", { type: "http", status: 429, discriminator: "quota", usage: "unreported" }],
		["invalid request", { type: "invalid-request", usage: "unreported" }],
		["context window", { type: "context-window", usage: "unreported" }],
		["unsupported", { type: "unsupported", usage: "unreported" }],
		["protocol invalid", { type: "protocol-invalid", usage: "unreported" }],
		["HTTP 409", { type: "http", status: 409, usage: "unreported" }],
	] as const)("keeps typed terminal %s failures on the current target", async (_name, fact) => {
		const first = routeFixture("primary", (model) => failureOutcome(model, "primary", fact));
		const second = routeFixture("secondary", (model) => successOutcome(model, "secondary"));
		const attempts = recordingAttempts();
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first, second], [target(first, ALL_TRANSIENT_FAILURES), target(second)]),
		});

		const stream = await attemptStream(modelStream)(first.model, CONTEXT, {}, attempts.lifecycle);
		const events = await collect(stream);

		expect(events.map((event) => event.type)).toEqual(["start", "error"]);
		expect(await stream.result()).toMatchObject({ provider: "primary", stopReason: "error" });
		expect(second.observed.streams).toBe(0);
		expect(attempts.records).toHaveLength(1);
		expect(attempts.records[0]?.settled).toBeUndefined();
	});

	test("does not infer fallback from error text, stack-like text, or an omitted typed fact", async () => {
		const first = routeFixture("primary", (model) =>
			failureOutcome(model, "primary", undefined, undefined, "429 quota timeout ECONNRESET 500"),
		);
		const second = routeFixture("secondary", (model) => successOutcome(model, "secondary"));
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first, second], [target(first, ALL_TRANSIENT_FAILURES), target(second)]),
		});

		const stream = await attemptStream(modelStream)(first.model, CONTEXT, {}, recordingAttempts().lifecycle);
		const result = await stream.result();

		expect(result).toMatchObject({ provider: "primary", stopReason: "error" });
		expect(second.observed.streams).toBe(0);
	});

	test("records an uncategorized failed terminal as unreported usage", async () => {
		const first = routeFixture("primary", (model) => failureOutcome(model, "primary"));
		const attempts = recordingAttempts();
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first], [target(first)]),
		});

		const stream = await attemptStream(modelStream)(first.model, CONTEXT, {}, attempts.lifecycle);
		await stream.result();

		expect(attempts.records[0]?.captured).toMatchObject({ usage: "unreported" });
	});

	test("does not advance when the typed transient kind is absent from the target allowlist", async () => {
		const first = routeFixture("primary", (model) =>
			failureOutcome(model, "primary", { type: "timeout", usage: "unreported" }),
		);
		const second = routeFixture("secondary", (model) => successOutcome(model, "secondary"));
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first, second], [target(first, ["transport"]), target(second)]),
		});

		const result = await (
			await attemptStream(modelStream)(first.model, CONTEXT, {}, recordingAttempts().lifecycle)
		).result();

		expect(result).toMatchObject({ provider: "primary", stopReason: "error" });
		expect(second.observed.streams).toBe(0);
	});

	test.each([
		"text_start",
		"text_delta",
		"text_end",
		"thinking_start",
		"thinking_delta",
		"thinking_end",
		"toolcall_start",
		"toolcall_delta",
		"toolcall_end",
	] as const)("commits the route before %s and never falls back afterward", async (eventType) => {
		const first = routeFixture("primary", (model) =>
			failureOutcome(model, "primary", { type: "timeout", usage: "unreported" }, undefined, undefined, [
				semanticEvent(eventType, model),
			]),
		);
		const second = routeFixture("secondary", (model) => successOutcome(model, "secondary"));
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first, second], [target(first, ["timeout"]), target(second)]),
			health: {
				id: "fallback-provenance",
				requirement: "required",
				maxObservationAgeMs: 10,
				now: () => 1,
			},
		});

		const stream = await attemptStream(modelStream)(first.model, CONTEXT, {}, recordingAttempts().lifecycle);
		const events = await collect(stream);

		expect(events.map((event) => event.type)).toEqual(["start", eventType, "error"]);
		expect(second.observed.streams).toBe(0);
	});

	test.each(["content", "deferred"] as const)(
		"treats terminal %s as semantic even when the provider omitted block events",
		async (kind) => {
			const first = routeFixture("primary", (model) => {
				const outcome = failureOutcome(model, "primary", { type: "timeout", usage: "unreported" });
				const terminal = outcome.events.at(-1);
				if (terminal?.type !== "error") throw new Error("Expected a terminal error event.");
				if (kind === "content") terminal.error.content = [{ type: "text", text: "visible" }];
				else {
					terminal.error.deferred = {
						provider: model.provider,
						modelId: model.id,
						api: model.api,
						id: "operation",
					};
				}
				return outcome;
			});
			const second = routeFixture("secondary", (model) => successOutcome(model, "secondary"));
			const modelStream = createProviderProfileModelStream({
				configuration: () => configuration([first, second], [target(first, ["timeout"]), target(second)]),
			});

			const result = await (
				await attemptStream(modelStream)(first.model, CONTEXT, {}, recordingAttempts().lifecycle)
			).result();

			expect(result.provider).toBe("primary");
			expect(second.observed.streams).toBe(0);
		},
	);

	test("snapshots a buffered Pi start before the provider mutates its shared output", async () => {
		const first = routeFixture("primary", (model) => {
			const message = assistantMessage(model, "error");
			const partial = { ...message, stopReason: "pending" as const };
			const semantic = semanticEvent("text_start", model);
			semantic.partial = partial;
			const events: AssistantMessageEvent[] = [
				{ type: "start", partial },
				semantic,
				{ type: "error", reason: "error", error: message },
			];
			return {
				events,
				fact: { type: "timeout", usage: "unreported" },
				before(index: number) {
					if (index === 1) partial.content.push({ type: "text", text: "mutated-after-start" });
				},
			};
		});
		const second = routeFixture("secondary", (model) => successOutcome(model, "secondary"));
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first, second], [target(first, ["timeout"]), target(second)]),
		});

		const stream = await attemptStream(modelStream)(first.model, CONTEXT, {}, recordingAttempts().lifecycle);
		const events = await collect(stream);

		expect(events[0]).toMatchObject({ type: "start", partial: { content: [] } });
		expect(second.observed.streams).toBe(0);
	});

	test.each(["missing", "duplicate"] as const)(
		"treats a %s start envelope as terminal protocol failure",
		async (kind) => {
			const first = routeFixture("first", (model) => {
				const message = assistantMessage(model, "error");
				const events: AssistantMessageEvent[] =
					kind === "duplicate"
						? [
								{ type: "start", partial: pendingMessage(model) },
								{ type: "start", partial: pendingMessage(model) },
								{ type: "error", reason: "error", error: message },
							]
						: [semanticEvent("text_start", model), { type: "error", reason: "error", error: message }];
				return { events, fact: { type: "timeout", usage: "unreported" } };
			});
			const second = routeFixture("second", (model) => successOutcome(model, "second"));
			const modelStream = createProviderProfileModelStream({
				configuration: () => configuration([first, second], [target(first, ["timeout"]), target(second)]),
			});

			const stream = await attemptStream(modelStream)(first.model, CONTEXT, {}, recordingAttempts().lifecycle);

			expect((await collect(stream)).map((event) => event.type)).toEqual(["start", "error"]);
			expect(second.observed.streams).toBe(0);
		},
	);

	test.each(["provider", "api", "model", "reason"] as const)(
		"rejects a terminal %s mismatch without fallback",
		async (field) => {
			const first = routeFixture("first", (model) => {
				const outcome = failureOutcome(model, "first", { type: "timeout", usage: "unreported" });
				const terminal = outcome.events.at(-1);
				if (terminal?.type !== "error") throw new Error("Expected a terminal error event.");
				if (field === "reason") terminal.reason = "aborted";
				else terminal.error[field] = "other";
				return outcome;
			});
			const second = routeFixture("second", (model) => successOutcome(model, "second"));
			const modelStream = createProviderProfileModelStream({
				configuration: () => configuration([first, second], [target(first, ["timeout"]), target(second)]),
			});

			const result = await (
				await attemptStream(modelStream)(first.model, CONTEXT, {}, recordingAttempts().lifecycle)
			).result();

			expect(result).toMatchObject({ provider: "first", model: "first-model", stopReason: "error" });
			expect(second.observed.streams).toBe(0);
		},
	);

	test.each(["done-reason", "error-route"] as const)(
		"drops untrusted terminal metadata from a malformed %s",
		async (kind) => {
			const first = routeFixture("first", (model) => {
				const message = assistantMessage(model, kind === "done-reason" ? "stop" : "error", [], {
					input: 7,
					output: 3,
				});
				message.responseModel = "untrusted-model";
				message.responseId = "untrusted-response";
				if (kind === "done-reason") {
					return {
						events: [
							{ type: "start", partial: pendingMessage(model) },
							{ type: "done", reason: "length", message },
						],
					};
				}
				message.model = "other-model";
				return {
					events: [
						{ type: "start", partial: pendingMessage(model) },
						{ type: "error", reason: "error", error: message },
					],
					fact: { type: "timeout", usage: "reported" },
				};
			});
			const second = routeFixture("second", (model) => successOutcome(model, "second"));
			const attempts = recordingAttempts();
			const modelStream = createProviderProfileModelStream({
				configuration: () => configuration([first, second], [target(first, ALL_TRANSIENT_FAILURES), target(second)]),
			});

			const result = await (await attemptStream(modelStream)(first.model, CONTEXT, {}, attempts.lifecycle)).result();

			expect(result).toMatchObject({ provider: "first", model: "first-model", usage: { input: 0, output: 0 } });
			expect(result.responseModel).toBeUndefined();
			expect(result.responseId).toBeUndefined();
			expect(attempts.records[0]?.captured).toMatchObject({ usage: "unreported", message: result });
			expect(second.observed.streams).toBe(0);
		},
	);

	test("fails closed for every invalid terminal reason regardless of delivery", async () => {
		await fc.assert(
			fc.asyncProperty(malformedTerminal, fc.array(semanticEventType), async ({ delivery, reason }, semanticTypes) => {
				const first = routeFixture("first", (model) => {
					const message = assistantMessage(
						model,
						reason as AssistantMessage["stopReason"],
						[{ type: "text", text: "untrusted-content" }],
						{
							input: 7,
							output: 3,
						},
					);
					Object.assign(message.usage, {
						cacheRead: 5,
						cacheWrite: 6,
						cacheWrite1h: 7,
						reasoning: 8,
						totalTokens: 29,
						cost: { input: 1, output: 2, cacheRead: 3, cacheWrite: 4, total: 10 },
					});
					message.responseModel = "untrusted-model";
					message.responseId = "untrusted-response";
					message.deferred = {
						provider: model.provider,
						modelId: model.id,
						api: model.api,
						id: "untrusted-operation",
						data: { secret: true },
					};
					message.diagnostics = [
						{
							type: "untrusted-diagnostic",
							timestamp: 1,
							error: { message: "untrusted-error" },
							details: { secret: true },
						},
					];
					message.rawStopReason = "untrusted-stop-reason";
					message.endTurn = true;
					message.errorMessage = "untrusted-error-message";
					if (delivery === "result") {
						return {
							events: [
								{ type: "start", partial: pendingMessage(model) },
								...semanticTypes.map((type) => semanticEvent(type, model)),
							],
							endResult: message,
							fact: { type: "timeout", usage: "reported" },
						};
					}
					return {
						events: [
							{ type: "start", partial: pendingMessage(model) },
							...semanticTypes.map((type) => semanticEvent(type, model)),
							{
								type: delivery,
								reason,
								...(delivery === "done" ? { message } : { error: message }),
							} as unknown as AssistantMessageEvent,
						],
						fact: { type: "timeout", usage: "reported" },
					};
				});
				const second = routeFixture("second", (model) => successOutcome(model, "second"));
				const attempts = recordingAttempts();
				const modelStream = createProviderProfileModelStream({
					configuration: () => configuration([first, second], [target(first, ALL_TRANSIENT_FAILURES), target(second)]),
				});

				const stream = await attemptStream(modelStream)(first.model, CONTEXT, {}, attempts.lifecycle);
				const events = await collect(stream);
				const result = await stream.result();

				expect(events.map((event) => event.type)).toEqual(["start", ...semanticTypes, "error"]);
				expect(result).toMatchObject({
					role: "assistant",
					content: [],
					api: "fallback-test",
					provider: "first",
					model: "first-model",
					stopReason: "error",
					errorMessage: "Provider stream failed.",
					usage: {
						input: 0,
						output: 0,
						cacheRead: 0,
						cacheWrite: 0,
						totalTokens: 0,
						cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
					},
				});
				expect(result.usage.cacheWrite1h).toBeUndefined();
				expect(result.usage.reasoning).toBeUndefined();
				expect(result.responseModel).toBeUndefined();
				expect(result.responseId).toBeUndefined();
				expect(result.deferred).toBeUndefined();
				expect(result.diagnostics).toBeUndefined();
				expect(result.rawStopReason).toBeUndefined();
				expect(result.endTurn).toBeUndefined();
				expect(attempts.records[0]?.captured).toMatchObject({ usage: "unreported", message: result });
				expect(second.observed.streams).toBe(0);
			}),
		);
	});

	test("preserves every legal Pi terminal reason for event and result-only delivery", async () => {
		await fc.assert(
			fc.asyncProperty(legalTerminal, async ({ kind, delivery, reason }) => {
				let trustedMessage: AssistantMessage | undefined;
				const first = routeFixture("first", (model) => {
					const message = assistantMessage(model, reason, [{ type: "text", text: "trusted-content" }], {
						input: 7,
						output: 3,
					});
					Object.assign(message.usage, {
						cacheRead: 5,
						cacheWrite: 6,
						cacheWrite1h: 7,
						reasoning: 8,
						totalTokens: 29,
						cost: { input: 1, output: 2, cacheRead: 3, cacheWrite: 4, total: 10 },
					});
					message.responseModel = "trusted-model";
					message.responseId = "trusted-response";
					message.diagnostics = [{ type: "trusted-diagnostic", timestamp: 1 }];
					message.rawStopReason = "trusted-stop-reason";
					message.endTurn = true;
					if (reason === "deferred") {
						message.deferred = {
							provider: model.provider,
							modelId: model.id,
							api: model.api,
							id: "trusted-operation",
							data: { trusted: true },
						};
					}
					if (kind === "error") message.errorMessage = "trusted-error-message";
					trustedMessage = message;
					const terminal =
						kind === "done"
							? ({ type: "done", reason, message } as const)
							: ({ type: "error", reason, error: message } as const);
					return delivery === "event"
						? {
								events: [{ type: "start", partial: pendingMessage(model) }, terminal],
								...(kind === "error" ? { fact: { type: "timeout" as const, usage: "reported" as const } } : {}),
							}
						: {
								events: [{ type: "start", partial: pendingMessage(model) }],
								endResult: message,
								...(kind === "error" ? { fact: { type: "timeout" as const, usage: "reported" as const } } : {}),
							};
				});
				const modelStream = createProviderProfileModelStream({
					configuration: () => configuration([first], [target(first)]),
				});

				const stream = await attemptStream(modelStream)(first.model, CONTEXT, {}, recordingAttempts().lifecycle);
				const events = await collect(stream);
				const result = await stream.result();

				expect(events.map((event) => event.type)).toEqual(["start", kind]);
				expect(result).toEqual(trustedMessage);
			}),
		);
	});

	test.each([
		{ delivery: "done" as const, firstReason: "stop", laterReason: "error", messageReason: "error" as const },
		{ delivery: "error" as const, firstReason: "error", laterReason: "stop", messageReason: "stop" as const },
	])("captures a $delivery terminal envelope once before validation", async (attack) => {
		let reasonReads = 0;
		const first = routeFixture("first", (model) => {
			const message = assistantMessage(model, attack.messageReason);
			const terminal: Record<string, unknown> = {
				type: attack.delivery,
				...(attack.delivery === "done" ? { message } : { error: message }),
			};
			Object.defineProperty(terminal, "reason", {
				enumerable: true,
				get() {
					reasonReads += 1;
					return reasonReads === 1 ? attack.firstReason : attack.laterReason;
				},
			});
			return {
				events: [{ type: "start", partial: pendingMessage(model) }, terminal as unknown as AssistantMessageEvent],
				fact: { type: "timeout", usage: "reported" },
			};
		});
		const second = routeFixture("second", (model) => successOutcome(model, "second"));
		const attempts = recordingAttempts();
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first, second], [target(first, ["timeout"]), target(second)]),
		});

		const stream = await attemptStream(modelStream)(first.model, CONTEXT, {}, attempts.lifecycle);

		expect((await collect(stream)).map((event) => event.type)).toEqual(["start", "error"]);
		expect(await stream.result()).toMatchObject({
			provider: "first",
			stopReason: "error",
			content: [],
			usage: { input: 0, output: 0 },
		});
		expect(reasonReads).toBe(1);
		expect(second.observed.streams).toBe(0);
	});

	test("rejects a semantic partial routed to a different target", async () => {
		const first = routeFixture("first", (model) => {
			const partial = semanticEvent("text_start", model);
			partial.partial.model = "other-model";
			return failureOutcome(model, "first", { type: "timeout", usage: "unreported" }, undefined, undefined, [partial]);
		});
		const second = routeFixture("second", (model) => successOutcome(model, "second"));
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first, second], [target(first, ["timeout"]), target(second)]),
		});

		const stream = await attemptStream(modelStream)(first.model, CONTEXT, {}, recordingAttempts().lifecycle);

		expect((await collect(stream)).map((event) => event.type)).toEqual(["start", "error"]);
		expect(second.observed.streams).toBe(0);
	});

	test("exhausts exactly the captured target list and leaves the final failure for GameAgent settlement", async () => {
		const first = routeFixture("first", (model) =>
			failureOutcome(model, "first", { type: "timeout", usage: "unreported" }),
		);
		const second = routeFixture("second", (model) =>
			failureOutcome(model, "second", { type: "transport", usage: "unreported" }),
		);
		const attempts = recordingAttempts();
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first, second], [target(first, ["timeout"]), target(second, ["transport"])]),
		});

		const stream = await attemptStream(modelStream)(first.model, CONTEXT, {}, attempts.lifecycle);
		const events = await collect(stream);

		expect(events.map((event) => event.type)).toEqual(["start", "error"]);
		expect(await stream.result()).toMatchObject({ provider: "second", stopReason: "error" });
		expect(attempts.records.map(({ provider, settled }) => ({ provider, settled: settled?.stopReason }))).toEqual([
			{ provider: "first", settled: "error" },
			{ provider: "second", settled: undefined },
		]);
		expect(first.observed.streams).toBe(1);
		expect(second.observed.streams).toBe(1);
	});

	test("lets caller abort win before the first target without opening an attempt", async () => {
		const first = routeFixture("first", (model) => successOutcome(model, "first"));
		const controller = new AbortController();
		const reason = Object.freeze({ kind: "caller" });
		controller.abort(reason);
		const attempts = recordingAttempts();
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first], [target(first)]),
		});

		await expect(
			attemptStream(modelStream)(first.model, CONTEXT, { signal: controller.signal }, attempts.lifecycle),
		).rejects.toBe(reason);
		expect(attempts.records).toEqual([]);
		expect(first.observed.streams).toBe(0);
	});

	test("lets an in-stream abort remain terminal even if a transient fact is attached", async () => {
		const first = routeFixture("first", (model) => {
			const message = assistantMessage(model, "aborted");
			return {
				events: [
					{ type: "start", partial: pendingMessage(model) },
					{ type: "error", reason: "aborted", error: message },
				],
				fact: { type: "timeout", usage: "unreported" },
			};
		});
		const second = routeFixture("second", (model) => successOutcome(model, "second"));
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first, second], [target(first, ["timeout"]), target(second)]),
		});

		const result = await (
			await attemptStream(modelStream)(first.model, CONTEXT, {}, recordingAttempts().lifecycle)
		).result();

		expect(result.stopReason).toBe("aborted");
		expect(second.observed.streams).toBe(0);
	});

	test("preserves trusted usage and response identity when typed abort evidence upgrades an error terminal", async () => {
		const reason = Object.freeze({ kind: "provider-abort" });
		const first = routeFixture("first", (model) => {
			const outcome = failureOutcome(
				model,
				"first",
				{ type: "aborted", reason, usage: "reported" },
				{ input: 7, output: 3 },
			);
			const terminal = outcome.events.at(-1);
			if (terminal?.type !== "error") throw new Error("Expected a terminal error event.");
			terminal.error.responseModel = "served-model";
			terminal.error.responseId = "response-1";
			return outcome;
		});
		const second = routeFixture("second", (model) => successOutcome(model, "second"));
		const attempts = recordingAttempts();
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first, second], [target(first, ALL_TRANSIENT_FAILURES), target(second)]),
		});

		const result = await (await attemptStream(modelStream)(first.model, CONTEXT, {}, attempts.lifecycle)).result();

		expect(result).toMatchObject({
			provider: "first",
			stopReason: "aborted",
			responseModel: "served-model",
			responseId: "response-1",
			usage: { input: 7, output: 3 },
		});
		expect(attempts.records[0]?.captured).toMatchObject({ usage: "reported", message: result });
		expect(second.observed.streams).toBe(0);
	});

	test("stops between attempts when settlement observes caller abort", async () => {
		const first = routeFixture("first", (model) =>
			failureOutcome(model, "first", { type: "timeout", usage: "unreported" }),
		);
		const second = routeFixture("second", (model) => successOutcome(model, "second"));
		const controller = new AbortController();
		const reason = Object.freeze({ kind: "between-attempts" });
		const attempts = recordingAttempts(() => controller.abort(reason));
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first, second], [target(first, ["timeout"]), target(second)]),
		});

		const stream = await attemptStream(modelStream)(
			first.model,
			CONTEXT,
			{ signal: controller.signal },
			attempts.lifecycle,
		);
		const result = await stream.result();

		expect(controller.signal.reason).toBe(reason);
		expect(result.stopReason).toBe("aborted");
		expect(second.observed.streams).toBe(0);
	});

	test("does not dispatch the next target when failed-attempt settlement fails", async () => {
		const first = routeFixture("first", (model) =>
			failureOutcome(model, "first", { type: "timeout", usage: "unreported" }),
		);
		const second = routeFixture("second", (model) => successOutcome(model, "second"));
		const settlementFailure = new Error("response provenance unavailable");
		const attempts = recordingAttempts(() => {
			throw settlementFailure;
		});
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first, second], [target(first, ["timeout"]), target(second)]),
		});

		const result = await (await attemptStream(modelStream)(first.model, CONTEXT, {}, attempts.lifecycle)).result();

		expect(result).toMatchObject({ provider: "first", stopReason: "error" });
		expect(second.observed.streams).toBe(0);
	});

	test("does not call the next provider when its request commit fails", async () => {
		const first = routeFixture("first", (model) =>
			failureOutcome(model, "first", { type: "timeout", usage: "unreported" }),
		);
		const second = routeFixture("second", (model) => successOutcome(model, "second"));
		let attempts = 0;
		const lifecycle: GameProviderAttemptLifecycle = {
			begin: () => {
				attempts += 1;
				const index = attempts;
				return {
					index: String(index),
					discard: () => undefined,
					captureTerminal: () => undefined,
					commit: async (start) => {
						if (index === 2) throw new Error("request provenance unavailable");
						return await start();
					},
					settle: async () => undefined,
				};
			},
		};
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first, second], [target(first, ["timeout"]), target(second)]),
			health: {
				id: "fallback-provenance",
				requirement: "required",
				maxObservationAgeMs: 10,
				now: () => 1,
			},
		});

		const result = await (await attemptStream(modelStream)(first.model, CONTEXT, {}, lifecycle)).result();

		expect(result).toMatchObject({ provider: "first", stopReason: "error" });
		expect(attempts).toBe(2);
		expect(second.observed.streams).toBe(0);
		await expect(modelStream.healthDependencies()[0]?.inspect?.(new AbortController().signal)).resolves.toEqual({
			state: "declared",
		});
	});

	test.each(["semantic", "done", "result-only", "malformed-done", "malformed-error"] as const)(
		"lets caller abort win over a queued %s provider outcome",
		async (kind) => {
			const controller = new AbortController();
			const reason = Object.freeze({ kind: "during-attempt" });
			let startWasInspected = () => false;
			const first = routeFixture("first", (model) => {
				const start = inspectablePendingMessage(model);
				startWasInspected = start.wasInspected;
				const valid = assistantMessage(model, "stop", [], { input: 7, output: 3 });
				valid.responseModel = "served-model";
				valid.responseId = "response-1";
				const malformedDone: Extract<AssistantMessageEvent, { type: "done" }> = {
					type: "done",
					reason: "length",
					message: valid,
				};
				const malformedErrorMessage = assistantMessage(model, "error", [], { input: 7, output: 3 });
				malformedErrorMessage.model = "other-model";
				malformedErrorMessage.responseModel = "untrusted-model";
				malformedErrorMessage.responseId = "untrusted-response";
				const malformedError: Extract<AssistantMessageEvent, { type: "error" }> = {
					type: "error",
					reason: "error",
					error: malformedErrorMessage,
				};
				return {
					events: kind === "malformed-error" ? [malformedError] : [],
					...(kind === "malformed-error" ? { fact: { type: "timeout", usage: "reported" } as const } : {}),
					schedule(response) {
						queueMicrotask(() => response.push({ type: "start", partial: start.message }));
						void start.inspected.then(() => {
							controller.abort(reason);
							switch (kind) {
								case "semantic":
									response.push(semanticEvent("text_start", model));
									break;
								case "done":
									response.push({ type: "done", reason: "stop", message: valid });
									break;
								case "result-only":
									response.end(valid);
									break;
								case "malformed-done":
									response.push(malformedDone);
									break;
								case "malformed-error":
									response.push(malformedError);
									break;
							}
						});
					},
				};
			});
			const second = routeFixture("second", (model) => successOutcome(model, "second"));
			const attempts = recordingAttempts();
			const modelStream = createProviderProfileModelStream({
				configuration: () => configuration([first, second], [target(first, ALL_TRANSIENT_FAILURES), target(second)]),
			});

			const stream = await attemptStream(modelStream)(
				first.model,
				CONTEXT,
				{ signal: controller.signal },
				attempts.lifecycle,
			);

			expect((await collect(stream)).map((event) => event.type)).toEqual(["start", "error"]);
			expect(await stream.result()).toMatchObject({ provider: "first", stopReason: "aborted" });
			expect(startWasInspected()).toBe(true);
			expect(controller.signal.reason).toBe(reason);
			expect(second.observed.streams).toBe(0);
			if (kind === "done" || kind === "result-only") {
				expect(attempts.records[0]?.captured).toMatchObject({
					usage: "reported",
					message: {
						responseModel: "served-model",
						responseId: "response-1",
						usage: { input: 7, output: 3 },
					},
				});
			} else {
				expect(attempts.records[0]?.captured).toMatchObject({
					usage: "unreported",
					message: { usage: { input: 0, output: 0 } },
				});
				expect(attempts.records[0]?.captured?.message.responseModel).toBeUndefined();
				expect(attempts.records[0]?.captured?.message.responseId).toBeUndefined();
			}
		},
	);

	test("treats clean EOF without a typed terminal event as terminal protocol failure", async () => {
		const first = routeFixture("first", (model) => ({
			events: [{ type: "start", partial: pendingMessage(model) }],
			endWithoutTerminal: true,
		}));
		const second = routeFixture("second", (model) => successOutcome(model, "second"));
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first, second], [target(first, ALL_TRANSIENT_FAILURES), target(second)]),
		});

		const result = await (
			await attemptStream(modelStream)(first.model, CONTEXT, {}, recordingAttempts().lifecycle)
		).result();

		expect(result).toMatchObject({ provider: "first", stopReason: "error" });
		expect(second.observed.streams).toBe(0);
	});

	test("preserves Pi result-only completion after the provider iterator ends", async () => {
		const first = routeFixture("first", (model) => ({
			events: [{ type: "start", partial: pendingMessage(model) }],
			endResult: assistantMessage(model, "stop", [{ type: "text", text: "implicit terminal" }]),
		}));
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first], [target(first)]),
		});

		const stream = await attemptStream(modelStream)(first.model, CONTEXT, {}, recordingAttempts().lifecycle);

		expect((await collect(stream)).map((event) => event.type)).toEqual(["start", "done"]);
		expect(await stream.result()).toMatchObject({
			provider: "first",
			content: [{ type: "text", text: "implicit terminal" }],
		});
	});

	test("consumes queued semantic events before a result-only failure decision", async () => {
		const first = routeFixture("first", (model) => ({
			events: [{ type: "start", partial: pendingMessage(model) }, semanticEvent("text_start", model)],
			endResult: assistantMessage(model, "error"),
			fact: { type: "timeout", usage: "unreported" },
		}));
		const second = routeFixture("second", (model) => successOutcome(model, "second"));
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first, second], [target(first, ["timeout"]), target(second)]),
		});

		const stream = await attemptStream(modelStream)(first.model, CONTEXT, {}, recordingAttempts().lifecycle);

		expect((await collect(stream)).map((event) => event.type)).toEqual(["start", "text_start", "error"]);
		expect(second.observed.streams).toBe(0);
	});

	test("preflights Tool support for every captured fallback target before opening target one", async () => {
		const first = routeFixture("first", (model) => successOutcome(model, "first"));
		const second = routeFixture("second", (model) => successOutcome(model, "second"));
		const attempts = recordingAttempts();
		const modelStream = createProviderProfileModelStream({
			configuration: () =>
				configuration([first, second], [target(first, ["timeout"]), { ...target(second), supportsTools: false }]),
		});

		await expect(
			attemptStream(modelStream)(first.model, { messages: [], tools: [{} as never] }, {}, attempts.lifecycle),
		).rejects.toMatchObject({ code: "tools_unsupported" });
		expect(attempts.records).toEqual([]);
		expect(first.observed.streams).toBe(0);
		expect(second.observed.streams).toBe(0);
	});

	test("preflights image input for every captured fallback target before opening target one", async () => {
		const first = routeFixture("first", (model) => successOutcome(model, "first"), ["text", "image"]);
		const second = routeFixture("second", (model) => successOutcome(model, "second"));
		const attempts = recordingAttempts();
		const modelStream = createProviderProfileModelStream({
			configuration: () => configuration([first, second], [target(first, ["timeout"]), target(second)]),
		});

		await expect(
			attemptStream(modelStream)(
				first.model,
				{
					messages: [
						{
							role: "user",
							content: [{ type: "image", data: "aGk=", mimeType: "image/png" }],
							timestamp: 1,
						},
					],
				},
				{},
				attempts.lifecycle,
			),
		).rejects.toMatchObject({ code: "images_unsupported" });
		expect(attempts.records).toEqual([]);
		expect(first.observed.streams).toBe(0);
		expect(second.observed.streams).toBe(0);
	});

	test("captures and freezes the safe fallback allowlist without exposing the fact source", () => {
		const first = routeFixture("first", (model) => successOutcome(model, "first"));
		const fallbackOn: ProviderFallbackFailureKind[] = ["timeout", "transport"];
		const config = configuration([first], [target(first, fallbackOn)]);

		const snapshot = captureProviderProfileSnapshot(config);
		fallbackOn[0] = "capacity";

		expect(snapshot.targets[0]).toMatchObject({ fallbackOn: ["timeout", "transport"] });
		expect(Object.isFrozen(snapshot.targets[0]?.fallbackOn)).toBe(true);
		expect(JSON.stringify(snapshot)).not.toContain("failureEvidence");
	});
});

interface RouteOutcome {
	readonly events: readonly AssistantMessageEvent[];
	readonly fact?: ProviderFailureFact;
	readonly endResult?: AssistantMessage;
	readonly endWithoutTerminal?: boolean;
	readonly before?: (index: number) => void;
	readonly schedule?: (response: ReturnType<typeof createAssistantMessageEventStream>) => void;
}

interface RouteFixture {
	readonly provider: Provider;
	readonly model: Model<string>;
	readonly failureEvidence: ProviderFailureEvidenceReader;
	readonly observed: { streams: number; maxRetries: number[] };
}

function routeFixture(
	id: string,
	outcome: (model: Model<string>) => RouteOutcome,
	input: Model<string>["input"] = ["text"],
): RouteFixture {
	const evidence = createProviderFailureEvidenceChannel();
	const observed = { streams: 0, maxRetries: [] as number[] };
	const model: Model<string> = {
		id: `${id}-model`,
		name: `${id}-model`,
		api: "fallback-test",
		provider: id,
		baseUrl: `https://${id}.invalid`,
		reasoning: true,
		input,
		cost: { input: 1, output: 2, cacheRead: 3, cacheWrite: 4 },
		contextWindow: 8192,
		maxTokens: 1024,
	};
	const stream = (_model: Model<string>, _context: unknown, options?: { maxRetries?: number }) => {
		observed.streams += 1;
		if (options?.maxRetries !== undefined) observed.maxRetries.push(options.maxRetries);
		const selected = outcome(model);
		const terminal = selected.events.find((event) => event.type === "done" || event.type === "error");
		const terminalMessage =
			terminal?.type === "error" ? terminal.error : terminal?.type === "done" ? terminal.message : selected.endResult;
		if (terminalMessage && selected.fact) evidence.report(terminalMessage, selected.fact);
		const response = createAssistantMessageEventStream();
		if (selected.schedule) selected.schedule(response);
		else {
			queueMicrotask(() => {
				selected.events.forEach((event, index) => {
					selected.before?.(index);
					response.push(event);
				});
				if (selected.endResult) response.end(selected.endResult);
				else if (selected.endWithoutTerminal) response.end();
			});
		}
		return response;
	};
	const provider: Provider = {
		id,
		name: id,
		auth: { apiKey: { name: id, resolve: async () => ({ auth: {} }) } },
		getModels: () => [model],
		stream: stream as Provider["stream"],
		streamSimple: stream as Provider["streamSimple"],
	};
	return {
		provider,
		model,
		failureEvidence: evidence.reader,
		observed,
	};
}

function configuration(
	routes: readonly RouteFixture[],
	targets: ProviderProfileConfiguration["targets"],
): ProviderProfileConfiguration {
	return {
		profiles: routes.map(({ provider, failureEvidence }) => ({
			provider,
			credential: { kind: "none" },
			failureEvidence,
		})),
		targets,
	};
}

function target(
	route: RouteFixture,
	fallbackOn: readonly ProviderFallbackFailureKind[] = [],
): ProviderProfileConfiguration["targets"][number] {
	return {
		profileId: route.provider.id,
		modelId: route.model.id,
		supportsTools: true,
		pricing: { kind: "pi-catalog" },
		fallbackOn,
	};
}

function successOutcome(model: Model<string>, text: string): RouteOutcome {
	const message = assistantMessage(model, "stop", [{ type: "text", text }]);
	return {
		events: [
			{ type: "start", partial: pendingMessage(model) },
			{ type: "done", reason: "stop", message },
		],
	};
}

function failureOutcome(
	model: Model<string>,
	text: string,
	fact?: ProviderFailureFact,
	usage?: Readonly<{ input: number; output: number }>,
	errorMessage = `${text} failed`,
	between: readonly AssistantMessageEvent[] = [],
): RouteOutcome {
	const message = assistantMessage(model, "error", [], usage, errorMessage);
	return {
		events: [
			{ type: "start", partial: pendingMessage(model) },
			...between,
			{ type: "error", reason: "error", error: message },
		],
		...(fact === undefined ? {} : { fact }),
	};
}

function assistantMessage(
	model: Model<string>,
	stopReason: AssistantMessage["stopReason"],
	content: AssistantMessage["content"] = [],
	usage: Readonly<{ input: number; output: number }> = { input: 0, output: 0 },
	errorMessage?: string,
): AssistantMessage {
	return {
		role: "assistant",
		content,
		api: model.api,
		provider: model.provider,
		model: model.id,
		usage: {
			input: usage.input,
			output: usage.output,
			cacheRead: 0,
			cacheWrite: 0,
			totalTokens: usage.input + usage.output,
			cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
		},
		stopReason,
		...(errorMessage === undefined ? {} : { errorMessage }),
		timestamp: 1,
	};
}

function pendingMessage(model: Model<string>): AssistantMessage {
	return assistantMessage(model, "pending");
}

function inspectablePendingMessage(model: Model<string>): {
	readonly message: AssistantMessage;
	readonly inspected: Promise<void>;
	readonly wasInspected: () => boolean;
} {
	const message = pendingMessage(model);
	const modelId = message.model;
	let inspected = false;
	let markInspected: () => void = () => undefined;
	const inspection = new Promise<void>((resolve) => {
		markInspected = resolve;
	});
	Object.defineProperty(message, "model", {
		enumerable: true,
		get() {
			inspected = true;
			markInspected();
			return modelId;
		},
	});
	return { message, inspected: inspection, wasInspected: () => inspected };
}

function semanticEvent(
	type: Exclude<AssistantMessageEvent["type"], "start" | "done" | "error">,
	model: Model<string>,
): Exclude<AssistantMessageEvent, { type: "start" | "done" | "error" }> {
	const partial = pendingMessage(model);
	switch (type) {
		case "text_start":
			return { type, contentIndex: 0, partial };
		case "text_delta":
			return { type, contentIndex: 0, delta: "delta", partial };
		case "text_end":
			return { type, contentIndex: 0, content: "text", partial };
		case "thinking_start":
			return { type, contentIndex: 0, partial };
		case "thinking_delta":
			return { type, contentIndex: 0, delta: "delta", partial };
		case "thinking_end":
			return { type, contentIndex: 0, content: "thought", partial };
		case "toolcall_start":
			return { type, contentIndex: 0, partial };
		case "toolcall_delta":
			return { type, contentIndex: 0, delta: "{}", partial };
		case "toolcall_end":
			return {
				type,
				contentIndex: 0,
				toolCall: { type: "toolCall", id: "call", name: "tool", arguments: {} },
				partial,
			};
	}
}

function recordingAttempts(onSettle?: (message: AssistantMessage) => void): {
	readonly lifecycle: GameProviderAttemptLifecycle;
	readonly records: Array<{
		provider: string;
		pricing: unknown;
		committed: boolean;
		settled?: AssistantMessage;
		captured?: { message: AssistantMessage; usage: "reported" | "unreported" };
	}>;
} {
	const records: Array<{
		provider: string;
		pricing: unknown;
		committed: boolean;
		settled?: AssistantMessage;
		captured?: { message: AssistantMessage; usage: "reported" | "unreported" };
	}> = [];
	return {
		records,
		lifecycle: {
			begin(model, pricing) {
				const record: {
					provider: string;
					pricing: unknown;
					committed: boolean;
					settled?: AssistantMessage;
					captured?: { message: AssistantMessage; usage: "reported" | "unreported" };
				} = { provider: model.provider, pricing, committed: false };
				records.push(record);
				return {
					index: records.length.toString(),
					discard: () => undefined,
					captureTerminal: (message, usage) => {
						record.captured = { message, usage };
					},
					commit: async (start) => {
						record.committed = true;
						return await start();
					},
					settle: async (message) => {
						record.settled = message;
						onSettle?.(message);
					},
				};
			},
		},
	};
}

function attemptStream(modelStream: ReturnType<typeof createProviderProfileModelStream>): GameProviderAttemptStreamFn {
	if (typeof modelStream === "function") throw new TypeError("Expected an attempt-aware model stream.");
	return modelStream.stream;
}

async function collect(stream: Awaited<ReturnType<GameProviderAttemptStreamFn>>): Promise<AssistantMessageEvent[]> {
	const events: AssistantMessageEvent[] = [];
	for await (const event of stream) events.push(event);
	return events;
}
