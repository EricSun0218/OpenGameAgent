import { Value } from "typebox/value";
import { describe, expect, it } from "vitest";
import {
	DurableTranscriptRecordSchema,
	DurableTranscriptToolResultMessageSchema,
	DurableTranscriptUserMessageSchema,
	TRANSCRIPT_SCHEMA_VERSION,
} from "../src/index.js";

const imageReference = {
	type: "image-reference",
	attachmentId: `img_${"a".repeat(64)}`,
	mediaType: "image/png",
	bytes: 68,
	width: 1,
	height: 1,
	name: "pixel.png",
} as const;

describe("durable transcript schemas", () => {
	it("keeps image references in user parts while text input remains one text part", () => {
		expect(
			Value.Check(DurableTranscriptUserMessageSchema, {
				role: "user",
				contentFormat: "parts",
				content: [{ type: "text", text: "look" }, imageReference],
				timestamp: 1,
			}),
		).toBe(true);
		expect(
			Value.Check(DurableTranscriptUserMessageSchema, {
				role: "user",
				contentFormat: "text",
				content: [imageReference],
				timestamp: 1,
			}),
		).toBe(false);
	});

	it("keeps image references in durable Tool results", () => {
		const result = {
			role: "toolResult",
			toolCallId: "call-1",
			toolName: "inspect_scene",
			content: [{ type: "text", text: "frame" }, imageReference],
			isError: false,
			timestamp: 1,
		};
		expect(Value.Check(DurableTranscriptToolResultMessageSchema, result)).toBe(true);
		expect(
			Value.Check(DurableTranscriptToolResultMessageSchema, {
				...result,
				content: [{ ...imageReference, attachmentId: "img_bad" }],
			}),
		).toBe(false);
	});

	it("accepts durable provider operations without storing their placeholder message", () => {
		const coordinate = { sessionId: "session", actorId: "actor", runId: "run", turn: "0" };
		const opened = {
			schemaVersion: "6",
			type: "deferred.opened",
			coordinate,
			operationId: "operation-1",
			input: { inputId: "input-1", payload: ["scene", 1, true, null] },
			handle: {
				provider: "provider",
				modelId: "model",
				api: "test-api",
				id: "provider-operation",
				expiresAt: 2,
				pollAfterMs: 0,
				data: { cursor: "opaque" },
			},
			recordId: "record-2",
			sequence: "2",
		};
		expect(Value.Check(DurableTranscriptRecordSchema, opened)).toBe(true);
		expect(
			Value.Check(DurableTranscriptRecordSchema, {
				...opened,
				reportedTokensBeforeDeferred: 4,
			}),
		).toBe(true);
		for (const reportedTokensBeforeDeferred of [-1, 1.5, Number.MAX_SAFE_INTEGER + 1]) {
			expect(
				Value.Check(DurableTranscriptRecordSchema, {
					...opened,
					reportedTokensBeforeDeferred,
				}),
			).toBe(false);
		}
		expect(
			Value.Check(DurableTranscriptRecordSchema, {
				...opened,
				handle: { ...opened.handle, pollAfterMs: -1 },
			}),
		).toBe(false);
		expect(
			Value.Check(DurableTranscriptRecordSchema, {
				...opened,
				handle: { ...opened.handle, id: undefined },
			}),
		).toBe(false);

		expect(
			Value.Check(DurableTranscriptRecordSchema, {
				schemaVersion: "6",
				type: "deferred.completed",
				coordinate,
				operationId: "operation-1",
				openedRecordId: "record-2",
				message: {
					role: "assistant",
					content: [{ type: "text", text: "ready" }],
					api: "test-api",
					provider: "provider",
					model: "model",
					stopReason: "stop",
					timestamp: 3,
				},
				recordId: "record-3",
				sequence: "3",
			}),
		).toBe(true);
		expect(
			Value.Check(DurableTranscriptRecordSchema, {
				schemaVersion: "6",
				type: "deferred.abandoned",
				coordinate,
				operationId: "operation-1",
				openedRecordId: "record-2",
				reason: "cancelled",
				recordId: "record-3",
				sequence: "3",
			}),
		).toBe(true);
		expect(
			Value.Check(DurableTranscriptRecordSchema, {
				schemaVersion: "6",
				type: "deferred.run.closed",
				coordinate,
				operationId: "operation-1",
				inputId: "input-1",
				status: "completed",
				recordId: "record-4",
				sequence: "4",
			}),
		).toBe(true);
	});

	it("accepts transcript schema version 6 without retaining version 5", () => {
		const record = {
			schemaVersion: "6",
			type: "message.completed",
			coordinate: { sessionId: "session", actorId: "actor", runId: "run", turn: "0" },
			message: {
				role: "user",
				contentFormat: "parts",
				content: [imageReference],
				timestamp: 1,
			},
			recordId: "record-1",
			sequence: "1",
		};
		expect(TRANSCRIPT_SCHEMA_VERSION).toBe("6");
		expect(Value.Check(DurableTranscriptRecordSchema, record)).toBe(true);
		expect(Value.Check(DurableTranscriptRecordSchema, { ...record, schemaVersion: "5" })).toBe(false);
	});
});
