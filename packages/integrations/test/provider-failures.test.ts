import type { AssistantMessage } from "@earendil-works/pi-ai";
import { describe, expect, it } from "vitest";
import {
	classifyProviderFailure,
	createProviderFailureEvidenceChannel,
	isProviderFallbackEligible,
	readProviderFailureEvidence,
	type ProviderFailureClassification,
	type ProviderFailureKind,
} from "../src/provider-failures.js";

describe("provider failure classification", () => {
	it("classifies a typed ordinary HTTP 429 as throttled", () => {
		expect(classifyProviderFailure({ type: "http", status: 429, usage: "reported" })).toEqual({
			kind: "throttled",
		});
	});

	it("lets a typed quota discriminator override HTTP 429 throttling", () => {
		expect(classifyProviderFailure({ type: "http", status: 429, discriminator: "quota", usage: "reported" })).toEqual({
			kind: "quota",
		});
	});

	it.each([
		[401, undefined, "authentication"],
		[403, undefined, "permission"],
		[408, undefined, "timeout"],
		[400, undefined, "invalid-request"],
		[413, undefined, "invalid-request"],
		[500, undefined, "provider-internal"],
		[599, undefined, "provider-internal"],
		[503, "capacity", "capacity"],
		[409, undefined, "unknown"],
		[404, undefined, "unknown"],
	] as const)("maps typed HTTP %s with %s discriminator to %s", (status, discriminator, kind) => {
		expect(
			classifyProviderFailure({
				type: "http",
				status,
				usage: "reported",
				...(discriminator === undefined ? {} : { discriminator }),
			}),
		).toEqual({ kind });
	});

	it.each([
		"credential",
		"quota",
		"timeout",
		"transport",
		"capacity",
		"provider-internal",
		"invalid-request",
		"context-window",
		"unsupported",
		"protocol-invalid",
	] as const)("preserves a typed %s fact", (type) => {
		expect(classifyProviderFailure({ type, usage: "reported" })).toEqual({ kind: type });
	});

	it("preserves the exact reason from a typed abort fact", () => {
		const reason = Object.freeze({ source: "caller" });
		const classified = classifyProviderFailure({ type: "aborted", reason, usage: "unreported" });

		expect(classified).toEqual({ kind: "aborted", reason });
		if (classified.kind !== "aborted") throw new Error("Expected an aborted classification.");
		expect(classified.reason).toBe(reason);
	});

	it("lets an already-aborted caller signal win with its exact reason", () => {
		const controller = new AbortController();
		const reason = Object.freeze({ source: "caller-signal" });
		controller.abort(reason);

		const classified = classifyProviderFailure({ type: "timeout", usage: "reported" }, controller.signal);

		expect(classified).toEqual({ kind: "aborted", reason });
		if (classified.kind !== "aborted") throw new Error("Expected an aborted classification.");
		expect(classified.reason).toBe(reason);
	});

	it("does not inspect generic error messages, stacks, bodies, status fields, or codes", () => {
		const failure = new Error("429 quota timeout ECONNRESET 500");
		Object.defineProperties(failure, {
			message: { get: () => failOnRead("message") },
			stack: { get: () => failOnRead("stack") },
			body: { get: () => failOnRead("body") },
			status: { value: 429 },
			code: { value: "quota" },
		});

		expect(classifyProviderFailure(failure as never)).toEqual({ kind: "unknown" });
		expect(classifyProviderFailure({ status: 429, code: "quota", message: "localized throttling" } as never)).toEqual({
			kind: "unknown",
		});
		expect(classifyProviderFailure(undefined)).toEqual({ kind: "unknown" });
	});

	it.each(["throttled", "timeout", "transport", "capacity", "provider-internal"] as const)(
		"lets the host admit typed %s fallback",
		(kind) => {
			const classification = { kind } as const;

			expect(isProviderFallbackEligible(classification, new Set([kind]))).toBe(true);
			expect(isProviderFallbackEligible(classification, new Set())).toBe(false);
		},
	);

	it.each([
		"aborted",
		"credential",
		"authentication",
		"permission",
		"quota",
		"invalid-request",
		"context-window",
		"unsupported",
		"protocol-invalid",
		"unknown",
	] as const)("keeps terminal %s failures ineligible even if present in an unsafe host set", (kind) => {
		const classification = classificationFor(kind);
		const unsafeAllowlist = new Set<ProviderFailureKind>([kind]);

		expect(isProviderFallbackEligible(classification, unsafeAllowlist as never)).toBe(false);
	});

	it("binds typed evidence to the exact terminal identity and reader", () => {
		const first = createProviderFailureEvidenceChannel();
		const second = createProviderFailureEvidenceChannel();
		const terminal = terminalMessage();
		const sameShape = terminalMessage();
		first.report(terminal, { type: "timeout", usage: "unreported" });

		expect(readProviderFailureEvidence(first.reader, terminal)).toEqual({
			type: "timeout",
			usage: "unreported",
		});
		expect(readProviderFailureEvidence(first.reader, sameShape)).toBeUndefined();
		expect(readProviderFailureEvidence(second.reader, terminal)).toBeUndefined();
	});

	it("copies and freezes evidence when the Adapter reports it", () => {
		const channel = createProviderFailureEvidenceChannel();
		const terminal = terminalMessage();
		const fact = { type: "http", status: 429, discriminator: "capacity", usage: "reported" } as const;
		channel.report(terminal, fact);
		(fact as { status: number }).status = 500;

		const captured = readProviderFailureEvidence(channel.reader, terminal);
		expect(captured).toEqual({
			type: "http",
			status: 429,
			discriminator: "capacity",
			usage: "reported",
		});
		expect(Object.isFrozen(captured)).toBe(true);
	});
});

function classificationFor(kind: ProviderFailureKind): ProviderFailureClassification {
	return kind === "aborted" ? { kind, reason: undefined } : { kind };
}

function failOnRead(property: string): never {
	throw new Error(`${property} must not be read`);
}

function terminalMessage(): AssistantMessage {
	return {
		role: "assistant",
		content: [],
		api: "failure-test",
		provider: "failure-test",
		model: "failure-test",
		usage: {
			input: 0,
			output: 0,
			cacheRead: 0,
			cacheWrite: 0,
			totalTokens: 0,
			cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
		},
		stopReason: "error",
		timestamp: 1,
	};
}
