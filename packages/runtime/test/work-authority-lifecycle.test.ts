import { describe, expect, it, vi } from "vitest";
import { GameRuntime, InMemoryEventStore } from "../src/index.js";

const request = {
	sessionId: "session",
	actorId: "actor",
	scope: "work",
	generation: "1",
	operationId: "close",
	workRetirement: {
		retirementId: "retire",
		recordedAt: "2026-09-05T00:00:00.000Z",
		partition: { ownerId: "owner", timelineId: "world", generation: "1", sessionId: "session", actorId: "actor" },
	},
};
const limits = { maxActiveActors: 1, maxQueuedRuns: 1, maxInputBytes: 2, maxInputDepth: 1 };

describe("composite work retirement prerequisites", () => {
	it("rejects a missing host drain binding before closing run authority", async () => {
		const events = new InMemoryEventStore();
		const close = vi.spyOn(events, "closeAuthority");
		const runtime = new GameRuntime(events, limits);
		await expect(runtime.closeAuthority(request)).rejects.toThrow("drainWorkAuthority");
		expect(close).not.toHaveBeenCalled();
		expect(await events.readAuthorityClosure(request)).toBeUndefined();
	});
	it("does not pretend independent memory stores can commit a composite retirement atomically", async () => {
		const retireGoalAuthority = vi.fn();
		const events = new InMemoryEventStore({ retireGoalAuthority });
		const goalRetirement = {
			...request.workRetirement,
			stateRevision: "1",
			gameTime: { timelineId: "world", generation: "1", tick: "0" },
		};
		await expect(events.closeAuthority({ ...request, goalRetirement })).rejects.toThrow("SQLite");
		expect(retireGoalAuthority).not.toHaveBeenCalled();
		expect(await events.readAuthorityClosure(request)).toBeUndefined();
	});
});
