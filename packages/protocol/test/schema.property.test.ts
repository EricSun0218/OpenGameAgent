import fc from "fast-check";
import { describe, expect, it } from "vitest";
import { assertCanonicalEvent, canonicalizeJsonValue, ProtocolValidationError } from "../src/index.js";

const opaqueIdentifier = fc.stringMatching(/^[A-Za-z0-9._:-]+$/u);
const mismatchedVisibilityPair = fc.constantFrom(
	["public", "owner"],
	["public", "internal"],
	["owner", "public"],
	["owner", "internal"],
	["internal", "public"],
	["internal", "owner"],
);
const distinctRecipientIds = fc.uniqueArray(opaqueIdentifier, { minLength: 2, maxLength: 4 });

describe("protocol schema properties", () => {
	it("rejects duplicate recipient identities in canonical audiences", () => {
		fc.assert(
			fc.property(opaqueIdentifier, (recipientId) => {
				const audience = { visibility: "recipient" as const, recipientIds: [recipientId, recipientId] };
				expect(() =>
					assertCanonicalEvent({
						protocolVersion: "1",
						eventId: "event-1",
						sequence: "1",
						revision: "1",
						occurredAt: "2026-09-01T00:00:00.000Z",
						coordinate: { sessionId: "session", actorId: "actor", runId: "run", turn: "0" },
						audience,
						data: {
							type: "item.completed",
							item: {
								itemType: "tool-call",
								itemId: "item-1",
								toolCallId: "tool-call-1",
								toolName: "world.read",
								audience,
							},
						},
					}),
				).toThrow(ProtocolValidationError);
			}),
		);
	});

	it("rejects recipient audiences with different lengths, members, or ordering", () => {
		fc.assert(
			fc.property(distinctRecipientIds, fc.constantFrom("length", "member", "order"), (recipientIds, mismatch) => {
				const itemAudience = { visibility: "recipient" as const, recipientIds };
				const rootRecipientIds = mismatchedRecipientIds(recipientIds, mismatch);
				const event = canonicalItemEvent(itemAudience);

				expect(() => assertCanonicalEvent({ ...event, audience: itemAudience })).not.toThrow();
				expect(() =>
					assertCanonicalEvent({
						...event,
						audience: { visibility: "recipient", recipientIds: rootRecipientIds },
					}),
				).toThrow(ProtocolValidationError);
			}),
		);
	});

	it("rejects canonical events whose root and item audiences disagree", () => {
		fc.assert(
			fc.property(mismatchedVisibilityPair, ([eventVisibility, itemVisibility]) => {
				const event = {
					protocolVersion: "1",
					eventId: "event-1",
					sequence: "1",
					revision: "1",
					occurredAt: "2026-09-01T00:00:00.000Z",
					coordinate: { sessionId: "session", actorId: "actor", runId: "run", turn: "0" },
					data: {
						type: "item.completed",
						item: {
							itemType: "message",
							itemId: "message-1",
							role: "assistant",
							content: [{ type: "text", text: "visible", sourceIndex: 0 }],
							audience: { visibility: itemVisibility },
						},
					},
				};
				expect(() => assertCanonicalEvent({ ...event, audience: { visibility: itemVisibility } })).not.toThrow();
				expect(() =>
					assertCanonicalEvent({
						...event,
						audience: { visibility: eventVisibility },
					}),
				).toThrow(ProtocolValidationError);
			}),
		);
	});

	it("keeps canonical wire bytes independent of object insertion order", () => {
		fc.assert(
			fc.property(fc.jsonValue().map(standardJsonRoundTrip), (value) => {
				expect(canonicalizeJsonValue(reverseObjectInsertionOrder(value))).toBe(canonicalizeJsonValue(value));
			}),
		);
	});
});

function standardJsonRoundTrip(value: unknown): unknown {
	return JSON.parse(JSON.stringify(value));
}

function canonicalItemEvent(audience: {
	readonly visibility: "internal" | "owner" | "public" | "recipient";
	readonly recipientIds?: readonly string[];
}) {
	return {
		protocolVersion: "1",
		eventId: "event-1",
		sequence: "1",
		revision: "1",
		occurredAt: "2026-09-01T00:00:00.000Z",
		coordinate: { sessionId: "session", actorId: "actor", runId: "run", turn: "0" },
		data: {
			type: "item.completed",
			item: {
				itemType: "tool-call",
				itemId: "item-1",
				toolCallId: "tool-call-1",
				toolName: "world.read",
				audience,
			},
		},
	};
}

function mismatchedRecipientIds(recipientIds: readonly string[], mismatch: "length" | "member" | "order"): string[] {
	if (mismatch === "length") return recipientIds.slice(0, -1);
	if (mismatch === "order") return [...recipientIds].reverse();
	const replacement = uniqueReplacement(recipientIds[0] ?? "recipient", recipientIds);
	return [replacement, ...recipientIds.slice(1)];
}

function uniqueReplacement(seed: string, existing: readonly string[]): string {
	let replacement = `${seed}#mismatch`;
	while (existing.includes(replacement)) replacement += "#";
	return replacement;
}

function reverseObjectInsertionOrder(value: unknown): unknown {
	if (Array.isArray(value)) return value.map(reverseObjectInsertionOrder);
	if (value === null || typeof value !== "object") return value;
	const reversed: Record<string, unknown> = {};
	for (const key of Object.keys(value).reverse()) {
		Object.defineProperty(reversed, key, {
			value: reverseObjectInsertionOrder(Reflect.get(value, key)),
			enumerable: true,
			configurable: true,
			writable: true,
		});
	}
	return reversed;
}
