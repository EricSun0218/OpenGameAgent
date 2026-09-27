import type { AssistantMessage } from "@earendil-works/pi-ai";
import type { GameProviderUsageObservation } from "@opengameagent/agent";

export type ProviderFailureKind =
	| "aborted"
	| "credential"
	| "authentication"
	| "permission"
	| "quota"
	| "throttled"
	| "timeout"
	| "transport"
	| "capacity"
	| "provider-internal"
	| "invalid-request"
	| "context-window"
	| "unsupported"
	| "protocol-invalid"
	| "unknown";

export type ProviderFallbackFailureKind = "throttled" | "timeout" | "transport" | "capacity" | "provider-internal";

export type ProviderFailureUsageObservation = GameProviderUsageObservation;

type ProviderFailureFactValue =
	| { type: "aborted"; reason: unknown }
	| {
			type: "http";
			status: number;
			discriminator?: "quota" | "capacity";
	  }
	| {
			type:
				| "credential"
				| "quota"
				| "timeout"
				| "transport"
				| "capacity"
				| "provider-internal"
				| "invalid-request"
				| "context-window"
				| "unsupported"
				| "protocol-invalid";
	  };

export type ProviderFailureFact = Readonly<ProviderFailureFactValue & { usage: ProviderFailureUsageObservation }>;

const PROVIDER_FAILURE_EVIDENCE_READER: unique symbol = Symbol("provider-failure-evidence-reader");

export interface ProviderFailureEvidenceReader {
	readonly [PROVIDER_FAILURE_EVIDENCE_READER]: true;
}

export interface ProviderFailureEvidenceChannel {
	readonly reader: ProviderFailureEvidenceReader;
	report(message: Readonly<AssistantMessage>, fact: ProviderFailureFact): void;
}

const evidenceByReader = new WeakMap<ProviderFailureEvidenceReader, WeakMap<AssistantMessage, ProviderFailureFact>>();

export function createProviderFailureEvidenceChannel(): ProviderFailureEvidenceChannel {
	const evidence = new WeakMap<AssistantMessage, ProviderFailureFact>();
	const reader = Object.freeze({ [PROVIDER_FAILURE_EVIDENCE_READER]: true as const });
	evidenceByReader.set(reader, evidence);
	return Object.freeze({
		reader,
		report(message: Readonly<AssistantMessage>, fact: ProviderFailureFact): void {
			evidence.set(message, captureFailureFact(fact));
		},
	});
}

export function readProviderFailureEvidence(
	reader: ProviderFailureEvidenceReader | undefined,
	message: Readonly<AssistantMessage>,
): ProviderFailureFact | undefined {
	return reader ? evidenceByReader.get(reader)?.get(message) : undefined;
}

export type ProviderFailureClassification =
	| Readonly<{ kind: "aborted"; reason: unknown }>
	| Readonly<{ kind: Exclude<ProviderFailureKind, "aborted"> }>;

export function classifyProviderFailure(
	fact: ProviderFailureFact | undefined,
	signal?: AbortSignal,
): ProviderFailureClassification {
	if (signal?.aborted) return { kind: "aborted", reason: signal.reason };
	if (fact?.type === "aborted") return { kind: "aborted", reason: fact.reason };
	if (fact?.type === "http") {
		if (fact.discriminator === "quota") return { kind: "quota" };
		if (fact.discriminator === "capacity") return { kind: "capacity" };
		if (fact.status === 401) return { kind: "authentication" };
		if (fact.status === 403) return { kind: "permission" };
		if (fact.status === 408) return { kind: "timeout" };
		if (fact.status === 429) return { kind: "throttled" };
		if (fact.status === 400 || fact.status === 413) return { kind: "invalid-request" };
		if (fact.status >= 500 && fact.status <= 599) return { kind: "provider-internal" };
		return { kind: "unknown" };
	}
	switch (fact?.type) {
		case "credential":
		case "quota":
		case "timeout":
		case "transport":
		case "capacity":
		case "provider-internal":
		case "invalid-request":
		case "context-window":
		case "unsupported":
		case "protocol-invalid":
			return { kind: fact.type };
		default:
			return { kind: "unknown" };
	}
}

export function isProviderFallbackEligible(
	classification: Readonly<ProviderFailureClassification>,
	allowlist: ReadonlySet<ProviderFallbackFailureKind>,
): boolean {
	switch (classification.kind) {
		case "throttled":
		case "timeout":
		case "transport":
		case "capacity":
		case "provider-internal":
			return allowlist.has(classification.kind);
		default:
			return false;
	}
}

export function isProviderFallbackFailureKind(value: unknown): value is ProviderFallbackFailureKind {
	switch (value) {
		case "throttled":
		case "timeout":
		case "transport":
		case "capacity":
		case "provider-internal":
			return true;
		default:
			return false;
	}
}

function captureFailureFact(fact: ProviderFailureFact): ProviderFailureFact {
	if (fact.type === "aborted") return Object.freeze({ type: fact.type, reason: fact.reason, usage: fact.usage });
	if (fact.type === "http") {
		return Object.freeze({
			type: fact.type,
			status: fact.status,
			...(fact.discriminator === undefined ? {} : { discriminator: fact.discriminator }),
			usage: fact.usage,
		});
	}
	return Object.freeze({ type: fact.type, usage: fact.usage });
}
