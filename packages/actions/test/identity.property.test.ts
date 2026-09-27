import fc from "fast-check";
import { describe, expect, it } from "vitest";
import { actionOperationId, type ActionIntentDraft, prepareActionIntent } from "../src/index.js";

const opaqueIdentifier = fc.stringMatching(/^[A-Za-z0-9._:-]+$/u);

describe("action identity properties", () => {
	it("captures every action operation identity field exactly once", () => {
		fc.assert(
			fc.property(opaqueIdentifier, (operationKey) => {
				const reads = new Map<string, number>();
				const input = Object.defineProperties(
					{},
					{
						sessionId: changingProperty(reads, "sessionId", "session", "changed-session"),
						actorId: changingProperty(reads, "actorId", "actor", "changed-actor"),
						coordinate: changingProperty(
							reads,
							"coordinate",
							{ sessionId: "session", actorId: "actor", runId: "run", turn: "1" },
							{ sessionId: "changed-session", actorId: "changed-actor", runId: "changed-run", turn: "2" },
						),
						actionName: changingProperty(reads, "actionName", "world.write", "world.changed"),
						operationKey: changingProperty(reads, "operationKey", operationKey, `${operationKey}:changed`),
					},
				) as {
					readonly sessionId: string;
					readonly actorId: string;
					readonly coordinate: ActionIntentDraft["coordinate"];
					readonly actionName: string;
					readonly operationKey: string;
				};

				const operationId = actionOperationId(input);

				expect(operationId).toBe(
					actionOperationId({
						sessionId: "session",
						actorId: "actor",
						coordinate: { sessionId: "session", actorId: "actor", runId: "run", turn: "1" },
						actionName: "world.write",
						operationKey,
					}),
				);
				expect(Object.fromEntries(reads)).toEqual({
					sessionId: 1,
					actorId: 1,
					coordinate: 1,
					actionName: 1,
					operationKey: 1,
				});
			}),
		);
	});

	it("derives every prepared field and fingerprint from one captured draft", () => {
		fc.assert(
			fc.property(opaqueIdentifier, (operationKey) => {
				const stable = stableDraft(operationKey);
				const expected = prepareActionIntent(stable);
				const reads = new Map<string, number>();
				const draft = getterBackedDraft(stable, reads);

				const prepared = prepareActionIntent(draft);

				expect(prepared).toEqual(expected);
				expect(prepared.operationId).toBe(actionOperationId(prepared));
				expect(Object.fromEntries(reads)).toEqual({
					"coordinate.sessionId": 1,
					"coordinate.actorId": 1,
					"coordinate.runId": 1,
					"coordinate.turn": 1,
					"authority.timelineId": 1,
					"authority.generation": 1,
					"authority.revision": 1,
					sessionId: 1,
					actorId: 1,
					coordinate: 1,
					actionName: 1,
					operationKey: 1,
					payload: 1,
					expectedAuthority: 1,
					conflictKeys: 1,
					replayPolicy: 1,
				});
			}),
		);
	});
});

function stableDraft(operationKey: string): ActionIntentDraft {
	return {
		sessionId: "session",
		actorId: "actor",
		coordinate: { sessionId: "session", actorId: "actor", runId: "run", turn: "0" },
		actionName: "world.write",
		operationKey,
		payload: { value: true },
		expectedAuthority: { timelineId: "world", generation: "1", revision: "2" },
		conflictKeys: ["world:tile:1"],
		replayPolicy: "recoverable",
	};
}

function getterBackedDraft(stable: ActionIntentDraft, reads: Map<string, number>): ActionIntentDraft {
	const coordinate = Object.defineProperties(
		{},
		{
			sessionId: changingProperty(reads, "coordinate.sessionId", stable.coordinate.sessionId, "changed-session"),
			actorId: changingProperty(reads, "coordinate.actorId", stable.coordinate.actorId, "changed-actor"),
			runId: changingProperty(reads, "coordinate.runId", stable.coordinate.runId, "changed-run"),
			turn: changingProperty(reads, "coordinate.turn", stable.coordinate.turn, "9"),
		},
	) as ActionIntentDraft["coordinate"];
	const authority = Object.defineProperties(
		{},
		{
			timelineId: changingProperty(reads, "authority.timelineId", stable.expectedAuthority.timelineId, "changed-world"),
			generation: changingProperty(reads, "authority.generation", stable.expectedAuthority.generation, "9"),
			revision: changingProperty(reads, "authority.revision", stable.expectedAuthority.revision, "9"),
		},
	) as ActionIntentDraft["expectedAuthority"];
	return Object.defineProperties(
		{},
		{
			sessionId: changingProperty(reads, "sessionId", stable.sessionId, "changed-session"),
			actorId: changingProperty(reads, "actorId", stable.actorId, "changed-actor"),
			coordinate: changingProperty(reads, "coordinate", coordinate, stable.coordinate),
			actionName: changingProperty(reads, "actionName", stable.actionName, "world.changed"),
			operationKey: changingProperty(reads, "operationKey", stable.operationKey, `${stable.operationKey}:changed`),
			payload: changingProperty(reads, "payload", stable.payload, { value: false }),
			expectedAuthority: changingProperty(reads, "expectedAuthority", authority, stable.expectedAuthority),
			conflictKeys: changingProperty(reads, "conflictKeys", stable.conflictKeys, ["changed:key"]),
			replayPolicy: changingProperty(reads, "replayPolicy", stable.replayPolicy, "never"),
		},
	) as ActionIntentDraft;
}

function changingProperty<T>(reads: Map<string, number>, key: string, first: T, later: T): PropertyDescriptor {
	return {
		enumerable: true,
		get() {
			const count = (reads.get(key) ?? 0) + 1;
			reads.set(key, count);
			return count === 1 ? first : later;
		},
	};
}
