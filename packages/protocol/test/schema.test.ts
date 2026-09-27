import { Value } from "typebox/value";
import { describe, expect, it } from "vitest";
import {
	assertCanonicalEvent,
	assertCanonicalEventData,
	assertCanonicalTimestamp,
	assertEventStreamCoordinate,
	assertGameTime,
	assertImageMediaType,
	assertOpaqueIdentifier,
	ActionApprovalRequestSchema,
	CanonicalEventDataSchema,
	ProtocolValidationError,
	RUNTIME_BASE_CAPABILITIES,
	RUNTIME_CAPABILITIES,
	RuntimeActionNextRequestSchema,
	RuntimeActionRecoveryResponseSchema,
	RuntimeActionReceiptRequestSchema,
	RuntimeActionReconcileRequestSchema,
	RuntimeAttachmentReadRequestSchema,
	RuntimeDeferredAbandonResponseSchema,
	RuntimeDeferredCommandRequestSchema,
	RuntimeDeferredReadResponseSchema,
	RuntimeApprovalListRequestSchema,
	RuntimeApprovalWaitRequestSchema,
	RuntimeApprovalWaitResponseSchema,
} from "../src/index.js";

describe("protocol schemas", () => {
	it("accepts only content-free exact run metrics", () => {
		const metrics = {
			type: "run.metrics",
			unit: "microseconds",
			segmentStatus: "completed",
			durations: {
				contextAndRecall: "100",
				provider: "200",
				durable: "30",
				native: "50",
				total: "350",
				firstResponse: "250",
			},
			counts: { providerAttempts: "1", retries: "0", fallbacks: "0", toolCalls: "0" },
		};
		expect(Value.Check(CanonicalEventDataSchema, metrics)).toBe(true);
		expect(() => assertCanonicalEventData(metrics)).not.toThrow();
		expect(() =>
			assertCanonicalEventData({ ...metrics, counts: { ...metrics.counts, providerAttempts: "2" } }),
		).not.toThrow();
		for (const durations of [
			{ ...metrics.durations, total: "349" },
			{ ...metrics.durations, durable: "351" },
			{ ...metrics.durations, firstResponse: "351" },
		])
			expect(() => assertCanonicalEventData({ ...metrics, durations })).toThrow(ProtocolValidationError);
		for (const counts of [
			{ ...metrics.counts, retries: "1" },
			{ ...metrics.counts, fallbacks: "1" },
		])
			expect(() => assertCanonicalEventData({ ...metrics, counts })).toThrow(ProtocolValidationError);
		expect(Value.Check(CanonicalEventDataSchema, { ...metrics, content: "private output" })).toBe(false);
		expect(
			Value.Check(CanonicalEventDataSchema, { ...metrics, durations: { ...metrics.durations, total: "01" } }),
		).toBe(false);
	});

	it("allows only reconciliation deliveries on the recovery endpoint", () => {
		const action = {
			sessionId: "session",
			actorId: "actor",
			operationId: `act_${"a".repeat(64)}`,
			intentFingerprint: "b".repeat(64),
			payloadFingerprint: "c".repeat(64),
			coordinate: { sessionId: "session", actorId: "actor", runId: "run", turn: "1" },
			actionName: "world.write",
			operationKey: "operation",
			payload: { value: 1 },
			expectedAuthority: { timelineId: "world", generation: "1", revision: "1" },
			conflictKeys: ["world:tile:1"],
			replayPolicy: "recoverable",
			status: "claimed",
			attempt: "1",
			createdAt: "2026-08-28T00:00:00.000Z",
			updatedAt: "2026-08-28T00:00:00.000Z",
			claim: {
				claimToken: "claim-token",
				claimantId: "claimant",
				attempt: "1",
				claimedAt: "2026-08-28T00:00:00.000Z",
				expiresAt: "2026-08-28T00:01:00.000Z",
			},
		};

		expect(Value.Check(RuntimeActionRecoveryResponseSchema, { delivery: { kind: "reconcile", action } })).toBe(true);
		expect(Value.Check(RuntimeActionRecoveryResponseSchema, { delivery: { kind: "dispatch", action } })).toBe(false);
	});

	it("accepts an exact run selector for Action delivery", () => {
		expect(Value.Check(RuntimeActionNextRequestSchema, { sessionId: "session", actorId: "actor", runId: "run" })).toBe(
			true,
		);
	});

	it("carries an optional JSON approval presentation without opening the request shape", () => {
		const request = {
			sessionId: "session",
			actorId: "actor",
			coordinate: { sessionId: "session", actorId: "actor", runId: "run", turn: "2" },
			approvalId: `apr_${"a".repeat(64)}`,
			requestFingerprint: "b".repeat(64),
			policyId: "high-risk",
			operationId: `act_${"c".repeat(64)}`,
			intentFingerprint: "d".repeat(64),
			actionName: "world.write",
			expectedAuthority: { timelineId: "world", generation: "3", revision: "9" },
			presentation: { kind: "game-choice", optionId: "opaque-option-7", label: "赠送礼物" },
			requestedAt: "2026-08-28T00:00:00.000Z",
			expiresAt: "2026-08-28T00:00:01.000Z",
		};

		expect(Value.Check(ActionApprovalRequestSchema, request)).toBe(true);
		expect(Value.Check(ActionApprovalRequestSchema, { ...request, coordinate: undefined })).toBe(false);
		expect(Value.Check(ActionApprovalRequestSchema, { ...request, hiddenReasoning: "private" })).toBe(false);
	});

	it("accepts an exact run selector for approval discovery", () => {
		expect(
			Value.Check(RuntimeApprovalListRequestSchema, {
				sessionId: "session",
				actorId: "actor",
				runId: "run",
				limit: 10,
			}),
		).toBe(true);
	});

	it("requires an exact run and bounded duration for approval waits", () => {
		const request = {
			sessionId: "session",
			actorId: "actor",
			runId: "run",
			limit: 10,
			waitMilliseconds: 1_000,
			afterCursor: "a".repeat(64),
		};
		expect(Value.Check(RuntimeApprovalWaitRequestSchema, request)).toBe(true);
		expect(Value.Check(RuntimeApprovalWaitRequestSchema, { ...request, runId: undefined })).toBe(false);
		expect(Value.Check(RuntimeApprovalWaitRequestSchema, { ...request, waitMilliseconds: 0 })).toBe(false);
		expect(Value.Check(RuntimeApprovalWaitResponseSchema, { approvals: [], cursor: "b".repeat(64) })).toBe(true);
	});

	it("keeps action receipt and reconciliation attempts strictly positive", () => {
		const fence = {
			sessionId: "session",
			actorId: "actor",
			operationId: `act_${"a".repeat(64)}`,
			claimToken: "claim-token",
		};
		expect(Value.Check(RuntimeActionReceiptRequestSchema, { ...fence, attempt: "1", outcome: "succeeded" })).toBe(true);
		expect(Value.Check(RuntimeActionReceiptRequestSchema, { ...fence, attempt: "0", outcome: "succeeded" })).toBe(
			false,
		);
		expect(Value.Check(RuntimeActionReconcileRequestSchema, { ...fence, attempt: "1", resolution: "applied" })).toBe(
			true,
		);
		expect(Value.Check(RuntimeActionReconcileRequestSchema, { ...fence, attempt: "0", resolution: "applied" })).toBe(
			false,
		);
	});

	it("accepts only protocol image media types", () => {
		for (const mediaType of ["image/png", "image/jpeg", "image/webp", "image/gif"]) {
			expect(() => assertImageMediaType(mediaType)).not.toThrow();
		}
		for (const mediaType of ["image/svg+xml", "https://private.invalid/image", "C:\\private\\image.png"]) {
			expect(() => assertImageMediaType(mediaType)).toThrow(ProtocolValidationError);
		}
	});

	it("accepts a minimal canonical event", () => {
		expect(() =>
			assertCanonicalEvent({
				protocolVersion: "1",
				eventId: "event-1",
				sequence: "1",
				revision: "1",
				occurredAt: "2026-08-28T00:00:00.000Z",
				coordinate: { sessionId: "session", actorId: "actor", runId: "run", turn: "0" },
				audience: { visibility: "owner" },
				data: { type: "run.started", inputId: "input" },
			}),
		).not.toThrow();
	});

	it("rejects control characters, numeric ambiguity, and unknown event properties", () => {
		expect(() => assertOpaqueIdentifier("bad\nidentifier")).toThrow(ProtocolValidationError);
		expect(() => assertCanonicalEventData({ type: "turn.started", extra: true })).toThrow(ProtocolValidationError);
		expect(() =>
			assertCanonicalEvent({
				protocolVersion: "1",
				eventId: "event-1",
				sequence: "01",
				revision: "1",
				occurredAt: "now",
				coordinate: { sessionId: "session", actorId: "actor", runId: "run", turn: "0" },
				audience: { visibility: "owner" },
				data: { type: "turn.started" },
			}),
		).toThrow(ProtocolValidationError);
	});

	it("validates exact deferred run lifecycle events", () => {
		expect(() => assertCanonicalEventData({ type: "run.suspended", operationId: "operation" })).not.toThrow();
		expect(() => assertCanonicalEventData({ type: "run.resumed", operationId: "operation" })).not.toThrow();
		expect(() => assertCanonicalEventData({ type: "run.suspended" })).toThrow(ProtocolValidationError);
		expect(() => assertCanonicalEventData({ type: "run.resumed", operationId: "operation", attempt: "1" })).toThrow(
			ProtocolValidationError,
		);
	});

	it("validates content-safe exact Tool-repeat policy events", () => {
		expect(() =>
			assertCanonicalEventData({
				type: "tool.repeat.detected",
				toolCallId: "call-3",
				toolName: "inspect",
				consecutiveCount: "3",
				action: "terminated",
			}),
		).not.toThrow();
		expect(() =>
			assertCanonicalEventData({
				type: "tool.repeat.detected",
				toolCallId: "call-2",
				toolName: "inspect",
				consecutiveCount: "02",
				action: "advisory",
			}),
		).toThrow(ProtocolValidationError);
		expect(() =>
			assertCanonicalEventData({
				type: "tool.repeat.detected",
				toolCallId: "call-0",
				toolName: "inspect",
				consecutiveCount: "0",
				action: "advisory",
			}),
		).toThrow(ProtocolValidationError);
		expect(() =>
			assertCanonicalEventData({
				type: "tool.repeat.detected",
				toolCallId: "call-2",
				toolName: "inspect",
				consecutiveCount: "2",
				action: "ignored",
			}),
		).toThrow(ProtocolValidationError);
	});

	it("validates exact game time without a JavaScript integer ceiling", () => {
		expect(() =>
			assertGameTime({ timelineId: "world", generation: "2", tick: "9007199254740993123456789" }),
		).not.toThrow();
		expect(() => assertGameTime({ timelineId: "world", generation: "01", tick: "1" })).toThrow(ProtocolValidationError);
	});

	it("binds an event coordinate to its authoritative stream", () => {
		const stream = { sessionId: "session", actorId: "actor" };
		expect(() => assertEventStreamCoordinate(stream, { ...stream, runId: "run", turn: "0" })).not.toThrow();
		expect(() =>
			assertEventStreamCoordinate(stream, { sessionId: "other", actorId: "actor", runId: "run", turn: "0" }),
		).toThrow(ProtocolValidationError);
		expect(() =>
			assertEventStreamCoordinate(stream, { sessionId: "session", actorId: "other", runId: "run", turn: "0" }),
		).toThrow(ProtocolValidationError);
	});

	it("binds context provenance to internal user messages", () => {
		const context = {
			contextVersion: "1",
			fingerprint: "a".repeat(64),
			references: [
				{
					sourceId: "memory",
					referenceId: "memory-1",
					revision: "1",
					contentFingerprint: "b".repeat(64),
				},
			],
		};
		const item = {
			itemType: "message",
			itemId: "context-1",
			role: "user",
			content: [{ type: "text", text: "{}" }],
			audience: { visibility: "internal" },
			context,
		};
		expect(() => assertCanonicalEventData({ type: "item.completed", item })).not.toThrow();
		expect(() => assertCanonicalEventData({ type: "item.completed", item: { ...item, role: "assistant" } })).toThrow(
			ProtocolValidationError,
		);
		expect(() =>
			assertCanonicalEventData({ type: "item.completed", item: { ...item, audience: { visibility: "owner" } } }),
		).toThrow(ProtocolValidationError);
	});

	it("validates provider-classified reasoning summaries as assistant-only content", () => {
		const item = {
			itemType: "message",
			itemId: "assistant-1",
			role: "assistant",
			content: [
				{ type: "reasoning-summary", text: "Checked the nearby threat.", sourceIndex: 0 },
				{ type: "text", text: "The path is clear.", sourceIndex: 1 },
			],
			audience: { visibility: "owner" },
		};
		expect(() => assertCanonicalEventData({ type: "item.completed", item })).not.toThrow();
		expect(() =>
			assertCanonicalEventData({
				type: "item.completed",
				item: { ...item, role: "user" },
			}),
		).toThrow(ProtocolValidationError);
		expect(() =>
			assertCanonicalEventData({
				type: "item.completed",
				item: {
					...item,
					content: [{ type: "reasoning-summary", text: "summary", sourceIndex: 0, signature: "secret" }],
				},
			}),
		).toThrow(ProtocolValidationError);
	});

	it("keeps durable image references in owner-visible and internal context messages", () => {
		const image = {
			type: "image-reference",
			attachmentId: `img_${"a".repeat(64)}`,
			mediaType: "image/png",
			bytes: 68,
			width: 1,
			height: 1,
			name: "pixel.png",
		};
		const item = {
			itemType: "message",
			itemId: "message-1",
			role: "user",
			content: [{ type: "text", text: "look" }, image],
			audience: { visibility: "owner" },
		};
		const { name: _name, ...namelessImage } = image;
		const context = {
			contextVersion: "1",
			fingerprint: "b".repeat(64),
			references: [
				{
					sourceId: "source",
					referenceId: "reference",
					revision: "1",
					contentFingerprint: "c".repeat(64),
				},
			],
		};
		expect(() => assertCanonicalEventData({ type: "item.completed", item })).not.toThrow();
		expect(() =>
			assertCanonicalEventData({
				type: "item.completed",
				item: { ...item, content: [{ ...image, attachmentId: "img_bad" }] },
			}),
		).toThrow(ProtocolValidationError);
		expect(() =>
			assertCanonicalEventData({
				type: "item.completed",
				item: {
					...item,
					content: [{ type: "image", data: "secret", mimeType: "image/png" }],
					audience: { visibility: "internal" },
					context,
				},
			}),
		).toThrow(ProtocolValidationError);
		const internalItem = {
			...item,
			content: [{ type: "text", text: "look" }, namelessImage],
			audience: { visibility: "internal" },
			context,
		};
		expect(() => assertCanonicalEventData({ type: "item.completed", item: internalItem })).not.toThrow();
		for (const content of [
			[{ type: "text", text: "look" }, image],
			[namelessImage],
			[namelessImage, { type: "text", text: "look" }],
			[
				{ type: "text", text: "look" },
				{ type: "text", text: "again" },
			],
		]) {
			expect(() => assertCanonicalEventData({ type: "item.completed", item: { ...internalItem, content } })).toThrow(
				ProtocolValidationError,
			);
		}
	});

	it("advertises attachment reads only as an optional capability and validates exact references", () => {
		expect(RUNTIME_CAPABILITIES).toContain("attachments.read");
		expect(RUNTIME_BASE_CAPABILITIES).not.toContain("attachments.read");
		const request = {
			sessionId: "session",
			actorId: "actor",
			reference: {
				type: "image-reference",
				attachmentId: `img_${"a".repeat(64)}`,
				mediaType: "image/png",
				bytes: 8,
				width: 1,
				height: 1,
			},
		};
		expect(Value.Check(RuntimeAttachmentReadRequestSchema, request)).toBe(true);
		expect(Value.Check(RuntimeAttachmentReadRequestSchema, { ...request, extra: true })).toBe(false);
		expect(
			Value.Check(RuntimeAttachmentReadRequestSchema, {
				...request,
				reference: { ...request.reference, attachmentId: "img_bad" },
			}),
		).toBe(false);
	});

	it("advertises and strictly validates the safe deferred lifecycle transport", () => {
		expect(RUNTIME_CAPABILITIES).toContain("deferred.lifecycle");
		expect(RUNTIME_BASE_CAPABILITIES).toContain("deferred.lifecycle");
		const coordinate = { sessionId: "session", actorId: "actor", runId: "run", turn: "0" };
		const deferred = { operationId: "operation", inputId: "input", coordinate };

		expect(Value.Check(RuntimeDeferredReadResponseSchema, { deferred: null })).toBe(true);
		expect(Value.Check(RuntimeDeferredReadResponseSchema, { deferred })).toBe(true);
		expect(
			Value.Check(RuntimeDeferredReadResponseSchema, {
				deferred: { ...deferred, handle: { provider: "private" } },
			}),
		).toBe(false);
		expect(Value.Check(RuntimeDeferredReadResponseSchema, { deferred, payload: { private: true } })).toBe(false);

		const command = { sessionId: "session", actorId: "actor", operationId: "operation" };
		expect(Value.Check(RuntimeDeferredCommandRequestSchema, command)).toBe(true);
		expect(Value.Check(RuntimeDeferredCommandRequestSchema, { ...command, expectedRevision: "1" })).toBe(false);
		for (const providerCancellation of ["completed", "failed", "unavailable"]) {
			expect(
				Value.Check(RuntimeDeferredAbandonResponseSchema, { operationId: "operation", providerCancellation }),
			).toBe(true);
		}
		expect(
			Value.Check(RuntimeDeferredAbandonResponseSchema, {
				operationId: "operation",
				providerCancellation: "pending",
			}),
		).toBe(false);
	});

	it("requires exact millisecond UTC timestamps for stable event identity", () => {
		expect(() => assertCanonicalTimestamp("2026-08-28T00:00:00.000Z")).not.toThrow();
		for (const timestamp of [
			"2026-08-28",
			"2026-08-28T00:00:00Z",
			"2026-08-28T08:00:00.000+08:00",
			"2026-02-31T00:00:00.000Z",
			"+010000-01-01T00:00:00.000Z",
			"-000001-01-01T00:00:00.000Z",
		]) {
			expect(() => assertCanonicalTimestamp(timestamp)).toThrow(ProtocolValidationError);
		}
	});
});
