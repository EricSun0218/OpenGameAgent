import type { RunCoordinate } from "@opengameagent/protocol";
import { describe, expect, it } from "vitest";
import {
	ActionClaimConflictError,
	ActionCoordinator,
	ActionIntentConflictError,
	ActionNotFoundError,
	ActionStateConflictError,
	actionOperationId,
	assertActionDelivery,
	assertActionRecord,
	InMemoryActionJournal,
	prepareActionIntent,
} from "../src/index.js";

const FIRST_COORDINATE: RunCoordinate = {
	sessionId: "session",
	actorId: "actor-one",
	runId: "run-one",
	turn: "1",
};

function draft(
	operationKey: string,
	overrides: Partial<Parameters<ActionCoordinator["submit"]>[0]> = {},
): Parameters<ActionCoordinator["submit"]>[0] {
	return {
		sessionId: "session",
		actorId: "actor-one",
		coordinate: FIRST_COORDINATE,
		actionName: "world.write",
		operationKey,
		payload: { value: 1 },
		expectedAuthority: { timelineId: "world", generation: "2", revision: "7" },
		conflictKeys: ["world:tile:1"],
		replayPolicy: "recoverable",
		...overrides,
	};
}

function clock(initial = "2026-08-28T00:00:00.000Z") {
	let current = new Date(initial);
	return {
		now: () => new Date(current),
		advance(milliseconds: number) {
			current = new Date(current.getTime() + milliseconds);
		},
	};
}

function coordinator(journal: InMemoryActionJournal, time = clock(), tokens = ["claim-one", "claim-two"]) {
	let token = 0;
	return {
		coordinator: new ActionCoordinator(journal, {
			now: time.now,
			newClaimToken: () => tokens[token++] ?? `claim-${token}`,
		}),
		time,
	};
}

describe("ActionCoordinator", () => {
	it("purges only bounded, old terminal Actions whose Goal projection is settled", async () => {
		const time = clock();
		const journal = new InMemoryActionJournal({
			goalProjection: {
				maxBindingBytes: 1_024,
				maxPendingPerStream: 2,
				maxListBatchSize: 2,
				maxRecordBytes: 8_192,
			},
		});
		const { coordinator: actions } = coordinator(journal, time, ["claim-one", "claim-two", "claim-three"]);
		const first = await actions.submit(draft("purge-first", { conflictKeys: ["first"] }));
		const firstClaim = await actions.nextDelivery({
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		if (firstClaim?.kind !== "dispatch" || !firstClaim.action.claim) throw new Error("Expected first claim.");
		await actions.recordReceipt({
			...first,
			claimToken: firstClaim.action.claim.claimToken,
			attempt: firstClaim.action.claim.attempt,
			outcome: "succeeded",
		});
		time.advance(1);
		const goal = await actions.submit(
			draft("purge-goal", {
				conflictKeys: ["goal"],
				goalProjection: {
					goalId: "goal",
					partition: { ownerId: "npc", timelineId: "world", generation: "2" },
					stepId: "step",
					expectedRevision: "1",
					inputId: "goal-input",
				},
			}),
		);
		const goalClaim = await actions.nextDelivery({
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		if (goalClaim?.kind !== "dispatch" || !goalClaim.action.claim) throw new Error("Expected Goal claim.");
		await actions.recordReceipt({
			...goal,
			claimToken: goalClaim.action.claim.claimToken,
			attempt: goalClaim.action.claim.attempt,
			outcome: "succeeded",
		});
		time.advance(1);
		const pending = await actions.submit(draft("purge-pending", { conflictKeys: ["pending"] }));

		await expect(async () =>
			actions.purgeTerminal({
				sessionId: "session",
				actorId: "actor-one",
				beforeUpdatedAt: "2026-08-28T00:00:01.000Z",
				limit: 1,
				retainOperationIds: [first.operationId],
				retainBudget: { maxCount: 1, maxBytes: 67 },
			}),
		).rejects.toThrow("retainOperationIds exceeds retainBudget.maxBytes.");
		await expect(
			actions.purgeTerminal({
				sessionId: "session",
				actorId: "actor-one",
				beforeUpdatedAt: "2026-08-28T00:00:01.000Z",
				limit: 1,
				retainOperationIds: [first.operationId],
				retainBudget: { maxCount: 1, maxBytes: Buffer.byteLength(first.operationId, "utf8") },
			}),
		).resolves.toEqual([]);
		await expect(
			actions.purgeTerminal({
				sessionId: "session",
				actorId: "actor-one",
				beforeUpdatedAt: "2026-08-28T00:00:01.000Z",
				limit: 1,
				retainOperationIds: [],
				retainBudget: { maxCount: 0, maxBytes: 0 },
			}),
		).resolves.toEqual([first.operationId]);
		await expect(actions.get(first, first.operationId)).resolves.toBeUndefined();
		await expect(actions.get(goal, goal.operationId)).resolves.toMatchObject({ status: "succeeded" });
		await expect(actions.get(pending, pending.operationId)).resolves.toMatchObject({ status: "pending" });

		await actions.settleGoalProjection({
			...goal,
			intentFingerprint: goal.intentFingerprint,
			outcome: "advanced",
		});
		await expect(
			actions.purgeTerminal({
				sessionId: "session",
				actorId: "actor-one",
				beforeUpdatedAt: "2026-08-28T00:00:01.000Z",
				limit: 2,
				retainOperationIds: [],
				retainBudget: { maxCount: 0, maxBytes: 0 },
			}),
		).resolves.toEqual([goal.operationId]);
	});

	it("keeps otherwise identical Actions from different runs as distinct durable operations", async () => {
		const { coordinator: actions } = coordinator(new InMemoryActionJournal());
		const first = await actions.submit(draft("same-operation"));
		const second = await actions.submit(
			draft("same-operation", { coordinate: { ...FIRST_COORDINATE, runId: "run-two" } }),
		);

		expect(second.operationId).not.toBe(first.operationId);
		expect(second.intentFingerprint).not.toBe(first.intentFingerprint);
		await expect(actions.get(first, first.operationId)).resolves.toMatchObject({ coordinate: FIRST_COORDINATE });
		await expect(actions.get(second, second.operationId)).resolves.toMatchObject({
			coordinate: { ...FIRST_COORDINATE, runId: "run-two" },
		});
	});

	it("claims an Action only from the requested run", async () => {
		const journal = new InMemoryActionJournal();
		const { coordinator: actions } = coordinator(journal);
		const first = await actions.submit(draft("first-run", { conflictKeys: ["first-key"] }));
		const second = await actions.submit(
			draft("second-run", {
				coordinate: { ...FIRST_COORDINATE, runId: "run-two" },
				conflictKeys: ["second-key"],
			}),
		);

		await expect(
			actions.nextDelivery({
				sessionId: "session",
				actorId: "actor-one",
				runId: "run-two",
				claimantId: "game",
				leaseDurationMs: 1_000,
				candidateLimit: 1,
			}),
		).resolves.toMatchObject({ kind: "dispatch", action: { operationId: second.operationId } });
		await expect(actions.get(first, first.operationId)).resolves.toMatchObject({ status: "pending", attempt: "0" });
	});

	it("does not expire or reconcile a claim from another requested run", async () => {
		const time = clock();
		const { coordinator: actions } = coordinator(new InMemoryActionJournal(), time, ["first-claim", "second-claim"]);
		const first = await actions.submit(draft("claimed-run", { replayPolicy: "safe", conflictKeys: ["first-key"] }));
		const second = await actions.submit(
			draft("requested-run", {
				coordinate: { ...FIRST_COORDINATE, runId: "run-two" },
				conflictKeys: ["second-key"],
			}),
		);
		await actions.nextDelivery({
			sessionId: "session",
			actorId: "actor-one",
			runId: "run-one",
			claimantId: "game",
			leaseDurationMs: 10,
			candidateLimit: 1,
		});
		time.advance(11);

		await expect(
			actions.nextDelivery({
				sessionId: "session",
				actorId: "actor-one",
				runId: "run-two",
				claimantId: "game",
				leaseDurationMs: 10,
				candidateLimit: 1,
			}),
		).resolves.toMatchObject({ kind: "dispatch", action: { operationId: second.operationId } });
		await expect(actions.get(first, first.operationId)).resolves.toMatchObject({ status: "claimed", attempt: "1" });
	});

	it("recovers only the oldest unsettled Action without mutating delivery state", async () => {
		const time = clock();
		const { coordinator: actions } = coordinator(new InMemoryActionJournal(), time, ["claim-one"]);
		const claimed = await actions.submit(draft("claimed", { conflictKeys: ["claimed-key"] }));
		const pending = await actions.submit(draft("pending", { conflictKeys: ["pending-key"] }));
		const delivery = await actions.nextDelivery({
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 10,
			candidateLimit: 1,
		});
		if (delivery?.kind !== "dispatch") throw new Error("Expected a dispatched Action.");
		time.advance(11);

		const recovered = await Promise.all([
			actions.recoverDelivery({ sessionId: "session", actorId: "actor-one" }),
			actions.recoverDelivery({ sessionId: "session", actorId: "actor-one" }),
		]);

		expect(recovered).toEqual([
			{ kind: "reconcile", action: delivery.action },
			{ kind: "reconcile", action: delivery.action },
		]);
		await expect(actions.get(claimed, claimed.operationId)).resolves.toEqual(delivery.action);
		await expect(actions.get(pending, pending.operationId)).resolves.toMatchObject({ status: "pending", attempt: "0" });
		if (delivery.action.claim === undefined) throw new Error("Expected a claimed Action.");
		const uncertain = await actions.recordReceipt({
			sessionId: "session",
			actorId: "actor-one",
			operationId: claimed.operationId,
			claimToken: delivery.action.claim.claimToken,
			attempt: delivery.action.claim.attempt,
			outcome: "unknown",
		});
		await expect(actions.recoverDelivery({ sessionId: "session", actorId: "actor-one" })).resolves.toEqual({
			kind: "reconcile",
			action: uncertain,
		});
	});

	it("returns no recovery delivery without touching pending or unrelated streams", async () => {
		const { coordinator: actions } = coordinator(new InMemoryActionJournal());
		const pending = await actions.submit(draft("pending-only"));
		const otherActor = await actions.submit(
			draft("other-actor", {
				actorId: "actor-two",
				coordinate: { ...FIRST_COORDINATE, actorId: "actor-two" },
				conflictKeys: ["other-key"],
			}),
		);
		await actions.nextDelivery({
			sessionId: "session",
			actorId: "actor-two",
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});

		await expect(actions.recoverDelivery({ sessionId: "session", actorId: "actor-one" })).resolves.toBeUndefined();
		await expect(
			actions.recoverDelivery({ sessionId: "other-session", actorId: "actor-two" }),
		).resolves.toBeUndefined();
		await expect(actions.get(pending, pending.operationId)).resolves.toMatchObject({ status: "pending", attempt: "0" });
		await expect(actions.get(otherActor, otherActor.operationId)).resolves.toMatchObject({
			status: "claimed",
			attempt: "1",
		});
	});

	it("waits for the exact durable Action to settle", async () => {
		const journal = new InMemoryActionJournal();
		const { coordinator: actions } = coordinator(journal);
		const submitted = await actions.submit(draft("wait-for-settlement"));
		const waiting = actions.waitForSettlement(submitted, { pollIntervalMs: 1 });
		const delivery = await actions.nextDelivery({
			sessionId: submitted.sessionId,
			actorId: submitted.actorId,
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		if (delivery?.kind !== "dispatch" || delivery.action.claim === undefined) {
			throw new Error("Expected an Action delivery.");
		}
		const settled = await actions.recordReceipt({
			sessionId: submitted.sessionId,
			actorId: submitted.actorId,
			operationId: submitted.operationId,
			claimToken: delivery.action.claim.claimToken,
			attempt: delivery.action.claim.attempt,
			outcome: "succeeded",
			result: { applied: true },
		});

		await expect(waiting).resolves.toEqual(settled);
	});

	it("cancels an Action wait with the caller's exact reason", async () => {
		const { coordinator: actions } = coordinator(new InMemoryActionJournal());
		const submitted = await actions.submit(draft("cancel-wait"));
		const controller = new AbortController();
		const reason = new Error("stop-waiting");
		controller.abort(reason);

		await expect(actions.waitForSettlement(submitted, { pollIntervalMs: 1, signal: controller.signal })).rejects.toBe(
			reason,
		);
	});

	it("fails closed when a waited Action is absent from the durable authority", async () => {
		const source = coordinator(new InMemoryActionJournal()).coordinator;
		const missing = await source.submit(draft("missing-wait"));
		const target = coordinator(new InMemoryActionJournal()).coordinator;

		await expect(target.waitForSettlement(missing, { pollIntervalMs: 1 })).rejects.toBeInstanceOf(ActionNotFoundError);
	});

	it("rereads a terminal Action from the exact durable authority", async () => {
		const sourceJournal = new InMemoryActionJournal();
		const source = coordinator(sourceJournal).coordinator;
		const submitted = await source.submit(draft("terminal-missing-wait"));
		const delivery = await source.nextDelivery({
			sessionId: submitted.sessionId,
			actorId: submitted.actorId,
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		if (delivery?.kind !== "dispatch" || delivery.action.claim === undefined) {
			throw new Error("Expected an Action delivery.");
		}
		const settled = await source.recordReceipt({
			sessionId: submitted.sessionId,
			actorId: submitted.actorId,
			operationId: submitted.operationId,
			claimToken: delivery.action.claim.claimToken,
			attempt: delivery.action.claim.attempt,
			outcome: "succeeded",
		});
		const target = coordinator(new InMemoryActionJournal()).coordinator;

		await expect(target.waitForSettlement(settled, { pollIntervalMs: 1 })).rejects.toBeInstanceOf(ActionNotFoundError);
	});

	it("rejects a same-fingerprint Action returned for a different durable identity", async () => {
		const source = coordinator(new InMemoryActionJournal()).coordinator;
		const expected = await source.submit(draft("misrouted-wait"));
		const foreign = await source.submit(
			draft("misrouted-wait", {
				actorId: "actor-two",
				coordinate: { ...FIRST_COORDINATE, actorId: "actor-two", runId: "run-two" },
			}),
		);
		const journal = new InMemoryActionJournal();
		journal.get = async () => structuredClone(foreign);
		const target = coordinator(journal).coordinator;

		await expect(target.waitForSettlement(expected, { pollIntervalMs: 1 })).rejects.toBeInstanceOf(
			ActionStateConflictError,
		);
	});

	it("preserves cancellation that arrives while reading the durable Action", async () => {
		const source = coordinator(new InMemoryActionJournal()).coordinator;
		const submitted = await source.submit(draft("abort-durable-read"));
		let releaseRead: ((record: typeof submitted) => void) | undefined;
		const journal = new InMemoryActionJournal();
		journal.get = () =>
			new Promise((resolve) => {
				releaseRead = resolve;
			});
		const target = coordinator(journal).coordinator;
		const controller = new AbortController();
		const reason = new Error("abort-during-action-read");
		const waiting = target.waitForSettlement(submitted, { pollIntervalMs: 1, signal: controller.signal });
		controller.abort(reason);
		releaseRead?.(submitted);

		await expect(waiting).rejects.toBe(reason);
	});

	it("requires explicit positive projection limits and rejects oversized bindings before admission", async () => {
		expect(
			() =>
				new InMemoryActionJournal({
					goalProjection: {
						maxBindingBytes: 0,
						maxPendingPerStream: 1,
						maxListBatchSize: 1,
						maxRecordBytes: 1,
					},
				}),
		).toThrow("maxBindingBytes");
		const journal = new InMemoryActionJournal({
			goalProjection: {
				maxBindingBytes: 1,
				maxPendingPerStream: 1,
				maxListBatchSize: 1,
				maxRecordBytes: 8_192,
			},
		});
		const { coordinator: actions } = coordinator(journal);
		expect(() =>
			actions.submit(
				draft("goal-oversized", {
					goalProjection: {
						goalId: "goal-1",
						partition: {
							ownerId: "npc-1",
							timelineId: "world",
							generation: "2",
						},
						stepId: "step-1",
						expectedRevision: "3",
						inputId: "input-1",
					},
				}),
			),
		).toThrow("binding exceeds");
		await expect(actions.listGoalProjections({ sessionId: "session", actorId: "actor-one", limit: 2 })).rejects.toThrow(
			"page limit",
		);
	});

	it.each([
		["session", { sessionId: "other-session" }],
		["actor", { actorId: "other-actor" }],
		["timeline", { timelineId: "other-world" }],
		["generation", { generation: "99" }],
	] as const)("rejects a Goal projection whose %s partition does not match its exact Action", (_name, mismatch) => {
		const journal = new InMemoryActionJournal({
			goalProjection: {
				maxBindingBytes: 1_024,
				maxPendingPerStream: 1,
				maxListBatchSize: 1,
				maxRecordBytes: 8_192,
			},
		});
		const { coordinator: actions } = coordinator(journal);
		expect(() =>
			actions.submit(
				draft("goal-partition-mismatch", {
					goalProjection: {
						goalId: "goal-1",
						partition: {
							ownerId: "npc-1",
							timelineId: "world",
							generation: "2",
							sessionId: "session",
							actorId: "actor-one",
							...mismatch,
						},
						stepId: "step-1",
						expectedRevision: "3",
						inputId: "input-1",
					},
				}),
			),
		).toThrow("does not match");
	});

	it("atomically admits one bounded immutable Goal projection with its exact Action", async () => {
		const journal = new InMemoryActionJournal({
			goalProjection: {
				maxBindingBytes: 1_024,
				maxPendingPerStream: 1,
				maxListBatchSize: 2,
				maxRecordBytes: 8_192,
			},
		});
		const { coordinator: actions } = coordinator(journal);
		const goalProjection = {
			goalId: "goal-1",
			partition: {
				ownerId: "npc-1",
				timelineId: "world",
				generation: "2",
				sessionId: "session",
				actorId: "actor-one",
			},
			stepId: "step-1",
			expectedRevision: "3",
			inputId: "input-1",
		};
		const admitted = actions.submit(draft("goal-bound", { goalProjection }));
		goalProjection.goalId = "mutated-goal";
		goalProjection.partition.ownerId = "mutated-owner";

		await expect(admitted).resolves.toMatchObject({
			goalProjection: {
				goalId: "goal-1",
				partition: { ownerId: "npc-1", sessionId: "session", actorId: "actor-one" },
				stepId: "step-1",
				expectedRevision: "3",
				inputId: "input-1",
			},
		});
		await expect(actions.submit(draft("goal-bound", { goalProjection }))).rejects.toThrow(ActionIntentConflictError);
		await expect(
			actions.submit(
				draft("goal-capacity", {
					goalProjection: { ...goalProjection, goalId: "goal-2", partition: { ...goalProjection.partition } },
				}),
			),
		).rejects.toThrow("Goal projection capacity");
		const rejectedOperationId = actionOperationId({
			sessionId: "session",
			actorId: "actor-one",
			coordinate: FIRST_COORDINATE,
			actionName: "world.write",
			operationKey: "goal-capacity",
		});
		await expect(actions.get(FIRST_COORDINATE, rejectedOperationId)).resolves.toBeUndefined();
	});

	it("enumerates bounded Goal projection recovery and releases capacity only after idempotent completion", async () => {
		const journal = new InMemoryActionJournal({
			goalProjection: {
				maxBindingBytes: 1_024,
				maxPendingPerStream: 1,
				maxListBatchSize: 1,
				maxRecordBytes: 8_192,
			},
		});
		const time = clock();
		const { coordinator: actions } = coordinator(journal, time);
		const goalProjection = {
			goalId: "goal-1",
			partition: {
				ownerId: "npc-1",
				timelineId: "world",
				generation: "2",
				sessionId: "session",
				actorId: "actor-one",
			},
			stepId: "step-1",
			expectedRevision: "3",
			inputId: "input-1",
		} as const;
		const submitted = await actions.submit(draft("goal-recovery", { goalProjection }));
		const delivery = await actions.nextDelivery({
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		if (delivery?.kind !== "dispatch" || !delivery.action.claim) throw new Error("Expected Action delivery.");
		await actions.recordReceipt({
			sessionId: "session",
			actorId: "actor-one",
			operationId: submitted.operationId,
			claimToken: delivery.action.claim.claimToken,
			attempt: delivery.action.claim.attempt,
			outcome: "succeeded",
			authorityAfter: { timelineId: "world", generation: "2", revision: "8" },
		});

		await expect(
			actions.listGoalProjections({ sessionId: "session", actorId: "actor-one", limit: 1 }),
		).resolves.toMatchObject({
			candidates: [{ sequence: "1", action: { operationId: submitted.operationId, goalProjection } }],
		});
		const completed = await actions.settleGoalProjection({
			sessionId: "session",
			actorId: "actor-one",
			operationId: submitted.operationId,
			intentFingerprint: submitted.intentFingerprint,
			outcome: "advanced",
		});
		expect(completed.goalProjectionSettlement).toEqual({
			outcome: "advanced",
			settledAt: "2026-08-28T00:00:00.000Z",
		});
		expect(completed.updatedAt).toBe("2026-08-28T00:00:00.000Z");
		await expect(
			actions.settleGoalProjection({
				sessionId: "session",
				actorId: "actor-one",
				operationId: submitted.operationId,
				intentFingerprint: submitted.intentFingerprint,
				outcome: "advanced",
			}),
		).resolves.toEqual(completed);
		await expect(
			actions.listGoalProjections({ sessionId: "session", actorId: "actor-one", limit: 1 }),
		).resolves.toEqual({ candidates: [] });
		await expect(actions.submit(draft("goal-after-completion", { goalProjection }))).resolves.toMatchObject({
			status: "pending",
		});
	});

	it("rejects an oversized Goal-bound receipt atomically before it can poison recovery", async () => {
		const journal = new InMemoryActionJournal({
			goalProjection: {
				maxBindingBytes: 1_024,
				maxPendingPerStream: 1,
				maxListBatchSize: 1,
				maxRecordBytes: 2_048,
			},
		});
		const { coordinator: actions } = coordinator(journal);
		const submitted = await actions.submit(
			draft("goal-record-budget", {
				goalProjection: {
					goalId: "goal-1",
					partition: { ownerId: "npc-1", timelineId: "world", generation: "2" },
					stepId: "step-1",
					expectedRevision: "3",
					inputId: "input-1",
				},
			}),
		);
		const delivery = await actions.nextDelivery({
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		if (delivery?.kind !== "dispatch" || !delivery.action.claim) throw new Error("Expected Action delivery.");

		await expect(
			actions.recordReceipt({
				sessionId: "session",
				actorId: "actor-one",
				operationId: submitted.operationId,
				claimToken: delivery.action.claim.claimToken,
				attempt: delivery.action.claim.attempt,
				outcome: "succeeded",
				result: { receipt: "x".repeat(4_096) },
			}),
		).rejects.toThrow("record exceeds");
		const retained = await actions.get(submitted, submitted.operationId);
		expect(retained).toMatchObject({ status: "claimed" });
		expect(retained?.receipt).toBeUndefined();
	});

	it("reserves durable settlement bytes before accepting a terminal Goal-bound receipt", async () => {
		const journal = new InMemoryActionJournal({
			goalProjection: {
				maxBindingBytes: 1_024,
				maxPendingPerStream: 1,
				maxListBatchSize: 1,
				maxRecordBytes: 1_250,
			},
		});
		const { coordinator: actions } = coordinator(journal);
		const submitted = await actions.submit(
			draft("goal-settlement-reserve", {
				goalProjection: {
					goalId: "goal-1",
					partition: { ownerId: "npc-1", timelineId: "world", generation: "2" },
					stepId: "step-1",
					expectedRevision: "3",
					inputId: "input-1",
				},
			}),
		);
		const delivery = await actions.nextDelivery({
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		if (delivery?.kind !== "dispatch" || !delivery.action.claim) throw new Error("Expected Action delivery.");

		await expect(
			actions.recordReceipt({
				sessionId: "session",
				actorId: "actor-one",
				operationId: submitted.operationId,
				claimToken: delivery.action.claim.claimToken,
				attempt: delivery.action.claim.attempt,
				outcome: "succeeded",
				result: { receipt: "x".repeat(100) },
			}),
		).rejects.toThrow("record exceeds");
		const retained = await actions.get(submitted, submitted.operationId);
		expect(retained).toMatchObject({ status: "claimed" });
		expect(retained?.receipt).toBeUndefined();
	});

	it("snapshots Goal projection list requests before journal validation and enumeration", async () => {
		const journal = new InMemoryActionJournal({
			goalProjection: {
				maxBindingBytes: 1_024,
				maxPendingPerStream: 1,
				maxListBatchSize: 1,
				maxRecordBytes: 8_192,
			},
		});
		const { coordinator: actions } = coordinator(journal);
		await actions.submit(
			draft("goal-list-snapshot", {
				goalProjection: {
					goalId: "goal-1",
					partition: { ownerId: "npc-1", timelineId: "world", generation: "2" },
					stepId: "step-1",
					expectedRevision: "3",
					inputId: "input-1",
				},
			}),
		);
		const reads = { sessionId: 0, actorId: 0, afterSequence: 0, limit: 0 };
		const counted =
			<Key extends keyof typeof reads, Value>(key: Key, value: Value) =>
			() => {
				reads[key] += 1;
				return value;
			};
		const request = Object.create(null) as Record<string, unknown>;
		Object.defineProperties(request, {
			sessionId: { enumerable: true, get: counted("sessionId", "session") },
			actorId: { enumerable: true, get: counted("actorId", "actor-one") },
			afterSequence: { enumerable: true, get: counted("afterSequence", "0") },
			limit: { enumerable: true, get: counted("limit", 1) },
		});

		await expect(actions.listGoalProjections(request as never)).resolves.toMatchObject({
			candidates: [{ action: { operationKey: "goal-list-snapshot" } }],
		});
		expect(reads).toEqual({ sessionId: 1, actorId: 1, afterSequence: 1, limit: 1 });
	});

	it("derives a stable full SHA-256 operation identity while binding changed content", async () => {
		const journal = new InMemoryActionJournal();
		const { coordinator: actions } = coordinator(journal);
		const first = await actions.submit(draft("stable-key"));
		const retry = await actions.submit(draft("stable-key", { payload: { value: 1 } }));

		expect(first.operationId).toMatch(/^act_[0-9a-f]{64}$/u);
		expect(retry).toEqual(first);
		await expect(actions.submit(draft("stable-key", { payload: { value: 2 } }))).rejects.toThrow(
			ActionIntentConflictError,
		);
	});

	it("rejects duplicate conflict keys instead of silently changing authority", () => {
		expect(() => prepareActionIntent(draft("duplicate", { conflictKeys: ["same", "same"] }))).toThrow(
			"Action conflict keys must be unique.",
		);
	});

	it("serializes cross-actor conflicts while allowing an unrelated dispatch after settlement", async () => {
		const journal = new InMemoryActionJournal();
		const { coordinator: actions } = coordinator(journal, clock(), ["claim-one", "claim-two", "claim-three"]);
		const first = await actions.submit(draft("first"));
		const second = await actions.submit(
			draft("second", {
				actorId: "actor-two",
				coordinate: { ...FIRST_COORDINATE, actorId: "actor-two", runId: "run-two" },
			}),
		);
		const unrelated = await actions.submit(draft("third", { conflictKeys: ["world:tile:2"] }));

		const firstDelivery = await actions.nextDelivery({
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 3,
		});
		if (firstDelivery?.kind !== "dispatch" || !firstDelivery.action.claim) {
			throw new Error("Expected the first dispatch.");
		}
		await expect(
			actions.nextDelivery({
				sessionId: "session",
				actorId: "actor-two",
				claimantId: "game",
				leaseDurationMs: 1_000,
				candidateLimit: 3,
			}),
		).resolves.toBeUndefined();
		await actions.recordReceipt({
			sessionId: "session",
			actorId: "actor-one",
			operationId: first.operationId,
			claimToken: firstDelivery.action.claim.claimToken,
			attempt: firstDelivery.action.claim.attempt,
			outcome: "succeeded",
		});

		await expect(
			actions.nextDelivery({
				sessionId: "session",
				actorId: "actor-two",
				claimantId: "game",
				leaseDurationMs: 1_000,
				candidateLimit: 3,
			}),
		).resolves.toMatchObject({ kind: "dispatch", action: { operationId: second.operationId } });
		await expect(
			actions.nextDelivery({
				sessionId: "session",
				actorId: "actor-one",
				claimantId: "game",
				leaseDurationMs: 1_000,
				candidateLimit: 3,
			}),
		).resolves.toMatchObject({ kind: "dispatch", action: { operationId: unrelated.operationId } });
	});

	it("returns the unresolved action for reconciliation before dispatching another action", async () => {
		const journal = new InMemoryActionJournal();
		const { coordinator: actions } = coordinator(journal, clock(), ["claim-one", "claim-two", "claim-three"]);
		const first = await actions.submit(draft("first", { conflictKeys: ["world:tile:1"] }));
		const second = await actions.submit(draft("second", { conflictKeys: ["world:tile:2"] }));
		const request = {
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 2,
		};

		await expect(actions.nextDelivery(request)).resolves.toMatchObject({
			kind: "dispatch",
			action: { operationId: first.operationId, status: "claimed", attempt: "1" },
		});
		const redelivery = await Promise.all([actions.nextDelivery(request), actions.nextDelivery(request)]);

		expect(redelivery).toEqual([
			expect.objectContaining({
				kind: "reconcile",
				action: expect.objectContaining({ operationId: first.operationId, attempt: "1" }),
			}),
			expect.objectContaining({
				kind: "reconcile",
				action: expect.objectContaining({ operationId: first.operationId, attempt: "1" }),
			}),
		]);
		await expect(actions.get(second, second.operationId)).resolves.toMatchObject({ status: "pending", attempt: "0" });
	});

	it("keeps an uncertain write as a conflict barrier until authoritative reconciliation", async () => {
		const journal = new InMemoryActionJournal();
		const time = clock();
		const { coordinator: actions } = coordinator(journal, time, ["claim-one", "claim-two"]);
		const first = await actions.submit(draft("first"));
		await actions.submit(draft("second"));
		const delivery = await actions.nextDelivery({
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 2,
		});
		if (delivery?.kind !== "dispatch" || !delivery.action.claim) throw new Error("Expected a dispatched action.");
		const claim = delivery.action.claim;
		const receiptInput = {
			sessionId: "session",
			actorId: "actor-one",
			operationId: first.operationId,
			claimToken: claim.claimToken,
			attempt: claim.attempt,
			outcome: "unknown" as const,
		};
		const uncertain = await actions.recordReceipt(receiptInput);
		time.advance(1);

		expect(uncertain).toMatchObject({ status: "uncertain", receipt: { outcome: "unknown" } });
		await expect(actions.recordReceipt(receiptInput)).resolves.toEqual(uncertain);
		await expect(
			actions.nextDelivery({
				sessionId: "session",
				actorId: "actor-one",
				claimantId: "game",
				leaseDurationMs: 1_000,
				candidateLimit: 2,
			}),
		).resolves.toMatchObject({ kind: "reconcile", action: { operationId: first.operationId } });
		await expect(
			actions.reconcile({
				sessionId: "session",
				actorId: "actor-one",
				operationId: first.operationId,
				claimToken: claim.claimToken,
				attempt: claim.attempt,
				resolution: "applied",
				result: { modelVisible: "done" },
			}),
		).resolves.toMatchObject({ status: "succeeded" });
		await expect(
			actions.nextDelivery({
				sessionId: "session",
				actorId: "actor-one",
				claimantId: "game",
				leaseDurationMs: 1_000,
				candidateLimit: 2,
			}),
		).resolves.toMatchObject({ kind: "dispatch", action: { status: "claimed" } });
	});

	it("recovers expired claims according to the declared replay policy", async () => {
		const time = clock();
		const journal = new InMemoryActionJournal();
		const { coordinator: actions } = coordinator(journal, time, ["safe-claim", "never-claim", "safe-redelivery"]);
		const safe = await actions.submit(draft("safe", { replayPolicy: "safe", conflictKeys: ["safe-key"] }));
		const never = await actions.submit(
			draft("never", {
				actorId: "actor-two",
				coordinate: { ...FIRST_COORDINATE, actorId: "actor-two", runId: "run-two" },
				replayPolicy: "never",
				conflictKeys: ["never-key"],
			}),
		);
		await actions.nextDelivery({
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 10,
			candidateLimit: 2,
		});
		await actions.nextDelivery({
			sessionId: "session",
			actorId: "actor-two",
			claimantId: "game",
			leaseDurationMs: 10,
			candidateLimit: 2,
		});
		time.advance(11);

		await expect(
			actions.nextDelivery({
				sessionId: "session",
				actorId: "actor-one",
				claimantId: "game",
				leaseDurationMs: 10,
				candidateLimit: 1,
			}),
		).resolves.toMatchObject({ kind: "dispatch", action: { operationId: safe.operationId, attempt: "2" } });
		await expect(
			actions.nextDelivery({
				sessionId: "session",
				actorId: "actor-two",
				claimantId: "game",
				leaseDurationMs: 10,
				candidateLimit: 1,
			}),
		).resolves.toMatchObject({ kind: "reconcile", action: { operationId: never.operationId, status: "uncertain" } });
	});

	it("requires the exact claim token and attempt for settlement", async () => {
		const journal = new InMemoryActionJournal();
		const { coordinator: actions } = coordinator(journal);
		const intent = await actions.submit(draft("stale"));
		const delivery = await actions.nextDelivery({
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		if (delivery?.kind !== "dispatch" || !delivery.action.claim) throw new Error("Expected a dispatched action.");
		const claim = delivery.action.claim;

		await expect(
			actions.recordReceipt({
				sessionId: "session",
				actorId: "actor-one",
				operationId: intent.operationId,
				claimToken: "stale-token",
				attempt: claim.attempt,
				outcome: "succeeded",
			}),
		).rejects.toThrow(ActionClaimConflictError);
	});

	it("replays an identical fenced receipt without replacing server audit time", async () => {
		const journal = new InMemoryActionJournal();
		const time = clock();
		const { coordinator: actions } = coordinator(journal, time);
		const intent = await actions.submit(draft("receipt-retry"));
		const delivery = await actions.nextDelivery({
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		if (delivery?.kind !== "dispatch" || !delivery.action.claim) throw new Error("Expected a dispatched action.");
		const claim = delivery.action.claim;
		const input = {
			sessionId: "session",
			actorId: "actor-one",
			operationId: intent.operationId,
			claimToken: claim.claimToken,
			attempt: claim.attempt,
			outcome: "succeeded" as const,
			result: { applied: true },
			authorityAfter: { timelineId: "world", generation: "2", revision: "8" },
		};
		const first = await actions.recordReceipt(input);
		time.advance(1);

		await expect(actions.recordReceipt(input)).resolves.toEqual(first);
		await expect(actions.recordReceipt({ ...input, result: { applied: false } })).rejects.toThrow(
			ActionStateConflictError,
		);
		await expect(actions.recordReceipt({ ...input, claimToken: "old-token" })).rejects.toThrow(
			ActionClaimConflictError,
		);
	});

	it("fences reconciliation from an expired attempt after atomic redelivery", async () => {
		const journal = new InMemoryActionJournal();
		const time = clock();
		const { coordinator: actions } = coordinator(journal, time, ["claim-one", "claim-two"]);
		const intent = await actions.submit(draft("redeliver", { replayPolicy: "safe" }));
		const request = {
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 10,
			candidateLimit: 1,
		};
		const first = await actions.nextDelivery(request);
		if (first?.kind !== "dispatch" || !first.action.claim) throw new Error("Expected the first dispatch.");
		time.advance(11);
		const second = await actions.nextDelivery(request);
		if (second?.kind !== "dispatch" || !second.action.claim) throw new Error("Expected the redelivery.");

		expect(second.action.attempt).toBe("2");
		await expect(
			actions.recordReceipt({
				sessionId: "session",
				actorId: "actor-one",
				operationId: intent.operationId,
				claimToken: first.action.claim.claimToken,
				attempt: first.action.claim.attempt,
				outcome: "succeeded",
			}),
		).rejects.toThrow(ActionClaimConflictError);
		await expect(
			actions.reconcile({
				sessionId: "session",
				actorId: "actor-one",
				operationId: intent.operationId,
				claimToken: first.action.claim.claimToken,
				attempt: first.action.claim.attempt,
				resolution: "applied",
			}),
		).rejects.toThrow(ActionClaimConflictError);
		await expect(
			actions.reconcile({
				sessionId: "session",
				actorId: "actor-one",
				operationId: intent.operationId,
				claimToken: second.action.claim.claimToken,
				attempt: second.action.claim.attempt,
				resolution: "applied",
			}),
		).resolves.toMatchObject({ status: "succeeded", attempt: "2" });
	});

	it("replays an identical fenced reconciliation without replacing server audit time", async () => {
		const journal = new InMemoryActionJournal();
		const time = clock();
		const { coordinator: actions } = coordinator(journal, time);
		const intent = await actions.submit(draft("reconcile-retry"));
		const delivery = await actions.nextDelivery({
			sessionId: "session",
			actorId: "actor-one",
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		if (delivery?.kind !== "dispatch" || !delivery.action.claim) throw new Error("Expected a dispatched action.");
		const claim = delivery.action.claim;
		const input = {
			sessionId: "session",
			actorId: "actor-one",
			operationId: intent.operationId,
			claimToken: claim.claimToken,
			attempt: claim.attempt,
			resolution: "applied" as const,
			result: { inspected: "applied" },
		};
		const first = await actions.reconcile(input);
		time.advance(1);

		await expect(actions.reconcile(input)).resolves.toEqual(first);
		await expect(actions.reconcile({ ...input, result: { inspected: "different" } })).rejects.toThrow(
			ActionStateConflictError,
		);
		await expect(actions.reconcile({ ...input, claimToken: "old-token" })).rejects.toThrow(ActionClaimConflictError);
	});

	it("rejects non-canonical action time before creating in-memory state", async () => {
		const journal = new InMemoryActionJournal();
		const prepared = prepareActionIntent(draft("timestamp"));
		await expect(journal.ensure(prepared, "2026-08-28")).rejects.toThrow();
		await expect(journal.get(prepared, prepared.operationId)).resolves.toBeUndefined();
		const record = await journal.ensure(prepared, "2026-08-28T00:00:00.000Z");
		expect(() => assertActionRecord(record)).not.toThrow();
	});

	it("rejects impossible Action delivery kind and state combinations at the public seam", async () => {
		const { coordinator: actions } = coordinator(new InMemoryActionJournal());
		const pending = await actions.submit(draft("delivery-semantics"));
		expect(() => assertActionDelivery({ kind: "reconcile", action: pending })).toThrow(ActionStateConflictError);

		const delivery = await actions.nextDelivery({
			sessionId: pending.sessionId,
			actorId: pending.actorId,
			claimantId: "game",
			leaseDurationMs: 1_000,
			candidateLimit: 1,
		});
		if (delivery?.kind !== "dispatch" || delivery.action.claim === undefined) {
			throw new Error("Expected an Action delivery.");
		}
		expect(() => assertActionDelivery(delivery)).not.toThrow();

		const succeeded = await actions.recordReceipt({
			sessionId: pending.sessionId,
			actorId: pending.actorId,
			operationId: pending.operationId,
			claimToken: delivery.action.claim.claimToken,
			attempt: delivery.action.claim.attempt,
			outcome: "succeeded",
		});
		expect(() => assertActionDelivery({ kind: "dispatch", action: succeeded })).toThrow(ActionStateConflictError);
	});
});
