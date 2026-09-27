import { RuntimeClient } from "@opengameagent/client";
import { RuntimeProtocolValidationError } from "@opengameagent/protocol";
import { describe, expect, it } from "vitest";

const request = {
	sessionId: "session",
	actorId: "actor",
	scope: "planning",
	generation: "1",
	operationId: "close",
	workRetirement: {
		retirementId: "work",
		recordedAt: "2026-09-05T00:00:00.000Z",
		partition: { ownerId: "owner", timelineId: "world", generation: "7", sessionId: "session", actorId: "actor" },
	},
};
const closure = {
	...request,
	closedAt: "2026-09-05T00:00:00.000Z",
	workRetirementResult: {
		phase: "finalized",
		cancelledScheduleCount: "9007199254740993",
		cancelledOccurrenceCount: "2",
		cancelledMessageCount: "3",
	},
};
function client(fetch: typeof globalThis.fetch) {
	return new RuntimeClient({
		baseUrl: "https://runtime.invalid",
		fetch,
		maxHttpResponseBytes: 4096,
		maxAttachmentResponseBytes: 4096,
		maxSseEventBytes: 4096,
	});
}

describe("Work authority client contract", () => {
	it("captures nested Work identity before transport and preserves decimal counts on close and read", async () => {
		const observed: unknown[] = [];
		const runtime = client(async (input) => {
			const outgoing = input as Request;
			observed.push(await outgoing.json());
			return Response.json(outgoing.url.endsWith("/read") ? { closure } : closure);
		});
		const mutable = structuredClone(request);
		const closing = runtime.closeAuthority(mutable);
		mutable.workRetirement.partition.ownerId = "other";
		mutable.workRetirement.retirementId = "other";
		expect((await closing).data).toEqual(closure);
		expect(observed[0]).toEqual(request);
		expect((await runtime.readAuthorityClosure(request)).data).toEqual({ closure });
	});
	it.each([
		{ ...closure, workRetirement: undefined },
		{ ...closure, workRetirementResult: undefined },
		{ ...closure, workRetirement: null, workRetirementResult: null },
		{
			...closure,
			workRetirement: {
				...request.workRetirement,
				partition: { ...request.workRetirement.partition, actorId: "other" },
			},
		},
		...(["phase", "cancelledScheduleCount", "cancelledOccurrenceCount", "cancelledMessageCount"] as const).flatMap(
			(field) =>
				(field === "phase" ? ["completed"] : [1, "01", "-1", "1.0", null]).map((value) => ({
					...closure,
					workRetirementResult: { ...closure.workRetirementResult, [field]: value },
				})),
		),
	])("rejects malformed Work closure on both response paths %#", async (response) => {
		const runtime = client(async (input) =>
			Response.json((input as Request).url.endsWith("/read") ? { closure: response } : response),
		);
		await expect(runtime.closeAuthority(request)).rejects.toBeInstanceOf(RuntimeProtocolValidationError);
		await expect(runtime.readAuthorityClosure(request)).rejects.toBeInstanceOf(RuntimeProtocolValidationError);
	});
	it("rejects a different retirement operation returned by close", async () => {
		const runtime = client(async () =>
			Response.json({ ...closure, workRetirement: { ...request.workRetirement, retirementId: "other" } }),
		);
		await expect(runtime.closeAuthority(request)).rejects.toBeInstanceOf(RuntimeProtocolValidationError);
	});
});
