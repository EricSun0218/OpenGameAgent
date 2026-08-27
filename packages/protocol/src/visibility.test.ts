import { describe, expect, it } from "vitest";
import type { GameAgentEvent, GameSessionKey } from "./runtime.js";
import { projectGameAgentEvent } from "./visibility.js";

const session: GameSessionKey = {
	worldId: "world",
	saveId: "save",
	timelineId: "timeline",
	generation: 1,
	ownerId: "owner-a",
	sessionId: "session",
	actorId: "actor",
};

const toolEvent: GameAgentEvent = {
	type: "tool.completed",
	sequence: 4,
	eventId: "run:4",
	runId: "run",
	turn: 1,
	audience: { visibility: "owner" },
	timestamp: 1,
	callId: "call",
	result: {
		content: [{ type: "json", value: { private: "detail" } }],
		details: { operationId: "secret-coordinate" },
		isError: false,
	},
};

describe("event audience projection", () => {
	it("enforces owner A, owner B, public, and internal visibility", () => {
		expect(projectGameAgentEvent(toolEvent, session, { principalId: "owner-b" })).toBeUndefined();
		expect(projectGameAgentEvent(toolEvent, session, {})).toBeUndefined();
		expect(projectGameAgentEvent(toolEvent, session, { principalId: "owner-a" })).toMatchObject({
			result: { content: [], isError: false },
		});
		expect(projectGameAgentEvent(toolEvent, session, { internal: true })).toEqual(toolEvent);
		expect(projectGameAgentEvent({ ...toolEvent, audience: { visibility: "public" } }, session, {})).toBeDefined();
	});

	it("never exposes tool arguments, progress, results, or details to non-internal viewers", () => {
		const started: GameAgentEvent = {
			...toolEvent,
			type: "tool.started",
			call: { id: "call", name: "write_world", arguments: { private: true } },
		};
		const progress: GameAgentEvent = { ...toolEvent, type: "tool.progress", callId: "call", update: { private: true } };
		expect(projectGameAgentEvent(started, session, { principalId: "owner-a" })).toMatchObject({
			call: { arguments: {} },
		});
		expect(projectGameAgentEvent(progress, session, { principalId: "owner-a" })).toMatchObject({ update: null });
		expect(JSON.stringify(projectGameAgentEvent(toolEvent, session, { principalId: "owner-a" }))).not.toContain(
			"secret-coordinate",
		);
	});
});
