import {
	assertRuntimeAuthorityClosure,
	assertRuntimeAuthorityReadResponse,
	captureCloseRunAuthorityRequest,
} from "@opengameagent/protocol";
import { describe, expect, it } from "vitest";

const request = {
	sessionId: "session",
	actorId: "actor",
	scope: "host-scope",
	generation: "1",
	operationId: "close",
	workRetirement: {
		retirementId: "retire",
		recordedAt: "2026-09-05T00:00:00.000Z",
		partition: { ownerId: "owner", timelineId: "timeline", generation: "1", sessionId: "session", actorId: "actor" },
	},
};

describe("work authority retirement capture", () => {
	it("captures and freezes the explicit exact lane before caller mutation", () => {
		const source = structuredClone(request);
		const captured = captureCloseRunAuthorityRequest(source);
		source.workRetirement.partition.actorId = "different";
		expect(captured).toEqual(request);
		expect(Object.isFrozen(captured.workRetirement?.partition)).toBe(true);
	});
	it.each(["ownerId", "timelineId", "generation", "sessionId", "actorId"])(
		"rejects a missing %s without widening the lane",
		(key) => {
			const source = structuredClone(request);
			Reflect.deleteProperty(source.workRetirement.partition, key);
			expect(() => captureCloseRunAuthorityRequest(source)).toThrow(TypeError);
		},
	);
	it("rejects a lane belonging to another run stream", () => {
		const source = structuredClone(request);
		source.workRetirement.partition.actorId = "other";
		expect(() => captureCloseRunAuthorityRequest(source)).toThrow(TypeError);
	});
	it("requires the retirement phase and request together in responses", () => {
		const closedAt = request.workRetirement.recordedAt;
		const workRetirementResult = {
			phase: "fenced",
			cancelledScheduleCount: "9007199254740993",
			cancelledOccurrenceCount: "0",
			cancelledMessageCount: "0",
		};
		expect(() => assertRuntimeAuthorityClosure({ ...request, closedAt })).toThrow(TypeError);
		expect(() => assertRuntimeAuthorityReadResponse({ closure: { ...request, closedAt } })).toThrow(TypeError);
		const { workRetirement: _work, ...withoutWork } = request;
		expect(() => assertRuntimeAuthorityClosure({ ...withoutWork, closedAt, workRetirementResult })).toThrow(TypeError);
		expect(() => assertRuntimeAuthorityClosure({ ...request, closedAt, workRetirementResult })).not.toThrow();
	});
});
