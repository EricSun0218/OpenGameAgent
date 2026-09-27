import type { StreamFn } from "@earendil-works/pi-agent-core";
import type { AssistantMessage, AssistantMessageEventStream } from "@earendil-works/pi-ai";
import {
	assertProviderRequestRecordId,
	type GameModelRequestProvenanceTemplate,
	prepareModelRequestAttemptProvenance,
	prepareModelResponseProvenance,
} from "./model-context-provenance.js";
import type { GameProvenanceAlias } from "./provenance-alias.js";
import type {
	GameProviderAttempt,
	GameProviderAttemptLifecycle,
	GameProviderPricingBasis,
	GameProviderProvenanceSink,
	GameProviderUsageObservation,
} from "./types.js";

const UNPRICED_BASIS: Readonly<GameProviderPricingBasis> = Object.freeze({ kind: "unpriced" });

export function createProviderAttemptLifecycle(options: {
	readonly sink?: GameProviderProvenanceSink;
	readonly requestTemplate?: GameModelRequestProvenanceTemplate;
	readonly now: () => number;
	readonly onCommit: (attempt: GameProviderAttempt, targetModel: Parameters<StreamFn>[0]) => void;
	readonly onSettle: (attempt: GameProviderAttempt) => void;
	readonly onResponse?: (message: AssistantMessage) => void;
	readonly onFailure?: () => void;
}): GameProviderAttemptLifecycle {
	let current: GameProviderAttempt | undefined;
	let committedCount = 0n;
	let provenanceFailed = false;
	const closeAfterProvenanceFailure = (): void => {
		current = undefined;
		if (provenanceFailed) return;
		provenanceFailed = true;
		options.onFailure?.();
	};
	return Object.freeze({
		begin(
			targetModel: Parameters<StreamFn>[0],
			pricingBasis?: Readonly<GameProviderPricingBasis>,
		): GameProviderAttempt {
			if (provenanceFailed) throw new TypeError("The provider-attempt lifecycle is closed.");
			if (current) throw new TypeError("The provider request already has an open attempt.");
			const capturedPricingBasis = capturePricingBasis(pricingBasis);
			const index = (committedCount + 1n).toString();
			const targetRoute = Object.freeze({
				provider: targetModel.provider,
				api: targetModel.api,
				id: targetModel.id,
			});
			let state: "draft" | "committing" | "committed" | "settling" | "settled" | "discarded" | "failed" = "draft";
			let recordId: GameProvenanceAlias | undefined;
			let capturedTerminal:
				| Readonly<{ message: AssistantMessage; usageObservation: GameProviderUsageObservation }>
				| undefined;
			const attempt: GameProviderAttempt = Object.freeze({
				index,
				captureTerminal(message: AssistantMessage, usageObservation: GameProviderUsageObservation): void {
					if (state !== "committed" || current !== attempt) {
						throw new TypeError("Only the committed provider attempt can capture its terminal response.");
					}
					if (message.stopReason !== "error" && message.stopReason !== "aborted") {
						throw new TypeError("Only an error or aborted provider response can be captured.");
					}
					if (capturedTerminal) throw new TypeError("The provider attempt already captured its terminal response.");
					capturedTerminal = { message, usageObservation };
				},
				discard(): void {
					if (state !== "draft" || current !== attempt) {
						throw new TypeError("Only an open provider-attempt draft can be discarded.");
					}
					state = "discarded";
					current = undefined;
				},
				async commit(start: () => ReturnType<StreamFn>): Promise<AssistantMessageEventStream> {
					if (state !== "draft" || current !== attempt) {
						throw new TypeError("Only an open provider-attempt draft can be committed.");
					}
					state = "committing";
					if (options.sink && options.requestTemplate) {
						const request = prepareModelRequestAttemptProvenance(
							options.requestTemplate,
							targetRoute,
							index,
							new Date(options.now()).toISOString(),
						);
						try {
							const committedId = await options.sink.recordRequest(request);
							assertProviderRequestRecordId(committedId);
							recordId = committedId;
						} catch (error) {
							state = "failed";
							closeAfterProvenanceFailure();
							throw error;
						}
					}
					state = "committed";
					committedCount += 1n;
					options.onCommit(attempt, targetModel);
					return await start();
				},
				async settle(message: AssistantMessage): Promise<void> {
					if (state !== "committed" || current !== attempt) {
						throw new TypeError("Only the committed provider attempt can be settled.");
					}
					if (capturedTerminal && !Object.is(capturedTerminal.message, message)) {
						throw new TypeError("The provider response does not match its captured terminal identity.");
					}
					options.onResponse?.(message);
					const usageObservation =
						capturedTerminal?.usageObservation ??
						(message.stopReason === "error" || message.stopReason === "aborted" ? "unreported" : "reported");
					const response = prepareModelResponseProvenance(
						message,
						targetRoute,
						new Date(options.now()).toISOString(),
						capturedPricingBasis,
						usageObservation,
					);
					state = "settling";
					if (options.sink && recordId) {
						try {
							await options.sink.recordResponse(recordId, response);
						} catch (error) {
							state = "failed";
							closeAfterProvenanceFailure();
							throw error;
						}
					}
					state = "settled";
					current = undefined;
					options.onSettle(attempt);
				},
			});
			current = attempt;
			return attempt;
		},
	});
}

function capturePricingBasis(
	value: Readonly<GameProviderPricingBasis> | undefined,
): Readonly<GameProviderPricingBasis> {
	if (value === undefined) return UNPRICED_BASIS;
	switch (value.kind) {
		case "pi-catalog":
		case "host-rate-card":
		case "free":
		case "unpriced":
			return Object.freeze({ kind: value.kind });
	}
}
